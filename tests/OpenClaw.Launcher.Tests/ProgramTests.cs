using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests;

public sealed class ProgramTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();
    private FakeMxcSessionClient? _lastSessionBackend;

    [Fact]
    public async Task AgentLaunchResolvesNodeAndRunsPackagedApplication()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        string[] arguments = ["gateway", "run", "--port", "12345"];
        var options = new HostOptions(applicationDirectory, null, arguments);
        var nodeRuntime = new NodeRuntime(
            Path.Combine(_testDirectory, "node.exe"),
            new Version(24, 15, 0),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        bool nodeResolutionAttempted = false;
        bool launchAttempted = false;

        int exitCode = await Program.RunAgentAsync(
            options,
            _ => { },
            _ =>
            {
                nodeResolutionAttempted = true;
                return Task.FromResult(nodeRuntime);
            },
            (
                nodePath,
                appDirectory,
                forwardedArguments,
                gatewayIsolationMode,
                _,
                _) =>
            {
                launchAttempted = true;
                Assert.Equal(nodeRuntime.ExecutablePath, nodePath);
                Assert.Equal(applicationDirectory, appDirectory);
                Assert.Equal(arguments, forwardedArguments);
                Assert.Equal(
                    GatewayIsolationMode.Disabled,
                    gatewayIsolationMode);
                return Task.FromResult(23);
            });

        Assert.True(nodeResolutionAttempted);
        Assert.True(launchAttempted);
        Assert.Equal(23, exitCode);
    }

    [Fact]
    public async Task SetupReportsAnUnavailableIsolatedSessionWithoutResolvingHostNode()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        string entryPoint = Path.Combine(applicationDirectory, "openclaw.mjs");
        await File.WriteAllTextAsync(entryPoint, "console.log('fixture');");
        DateTime lastWriteTime = File.GetLastWriteTimeUtc(entryPoint);
        var options = new HostOptions(applicationDirectory, null, []);
        var nodeRuntime = new NodeRuntime(
            Path.Combine(_testDirectory, "node.exe"),
            new Version(24, 15, 0),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            options,
            ["setup"],
            _ => { },
            output,
            TextWriter.Null,
            _ => Task.FromResult(nodeRuntime));

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(entryPoint));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(entryPoint));
        Assert.DoesNotContain(
            nodeRuntime.ExecutablePath,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            applicationDirectory,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "requires isolated-session support",
            output.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentUsesTheRuntimeInstalledForTheSessionWithoutHostFallback()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        HostOptions options = CreateSetupOptions(applicationDirectory);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime) { TeardownSucceeds = true };

        int setupExitCode = await Program.RunControlAsync(
            options,
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            installationLifecycle: lifecycle);
        Assert.Equal(0, setupExitCode);

        string expectedAgentNode = runtime.SetupState
            .Read(runtime.ApplicationId).Record!.AgentNodePath!;
        bool directLaunchAttempted = false;
        _lastSessionBackend!.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                "launch-*.json").Single();
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            Assert.Equal(expectedAgentNode, request.Executable);
            Assert.Equal(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                request.WorkingDirectory);
            Assert.Equal(
                "enabled",
                request.Environment![OpenClawRuntimeEnvironment.GatewayIsolationVariable]);
            return Task.FromResult(0);
        };

        await Assert.ThrowsAsync<SessionException>(() => Program.RunAgentAsync(
            options,
            _ => { },
            _ => throw new InvalidOperationException("Host Node must not be resolved."),
            (_, _, _, _, _, _) =>
            {
                directLaunchAttempted = true;
                return Task.FromResult(0);
            },
            _ => runtime,
            _ => Task.FromResult(new MxcReadinessReport(
                "runtime",
                null,
                null,
                MxcHostSupport.Supported,
                null,
                MxcSupportEvidence.HostBuild)),
            () => "OpenClaw.Gateway_test",
            _ => null));

        Assert.False(directLaunchAttempted);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("foreign")]
    [InlineData("mismatched")]
    public async Task AutomaticHostFallbackRefusesSavedOwnershipFailures(
        string stateKind)
    {
        SessionRuntime runtime = CreateSessionRuntime();
        string applicationDirectory = Path.Combine(_testDirectory, "fallback-app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "fixture").ConfigureAwait(true);
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.Paths.SessionStatePath)!);
        SessionRecord session = new()
        {
            ApplicationId = runtime.ApplicationId,
            SandboxId = "iso:saved",
            WorkspacePath = Path.Combine(_testDirectory, "workspace"),
            Generation = "test-generation",
            CreatedUtc = DateTimeOffset.UtcNow
        };
        Directory.CreateDirectory(session.WorkspacePath!);

        switch (stateKind)
        {
            case "corrupt":
                await File.WriteAllTextAsync(
                    runtime.Paths.SessionStatePath,
                    "{ not json").ConfigureAwait(true);
                break;
            case "foreign":
                await File.WriteAllTextAsync(
                    runtime.Paths.SessionStatePath,
                    """
                    {"schemaVersion":1,"sandboxId":"iso:foreign","applicationId":"PFN:Other","createdUtc":"2026-01-01T00:00:00Z"}
                    """).ConfigureAwait(true);
                break;
            default:
                new SessionStateStore(runtime.Paths.SessionStatePath).Write(session);
                runtime.SetupState.Write(new SetupRecord
                {
                    ApplicationId = runtime.ApplicationId,
                    SandboxId = "iso:different",
                    Phase = SetupPhase.Ready
                });
                break;
        }

        bool hostPrepared = false;
        bool hostLaunched = false;

        await Assert.ThrowsAsync<SessionException>(() => Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, ["--version"]),
            _ => { },
            _ =>
            {
                hostPrepared = true;
                return Task.FromResult(new NodeRuntime(
                    "node.exe",
                    new Version(24, 0),
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
            },
            (_, _, _, _, _, _) =>
            {
                hostLaunched = true;
                return Task.FromResult(0);
            },
            _ => runtime,
            _ => Task.FromResult(new MxcReadinessReport(
                null,
                null,
                "backend unavailable",
                MxcHostSupport.Unsupported,
                null,
                MxcSupportEvidence.HostBuild)),
            () => runtime.Paths.PackageFamilyName,
            _ => null));

        Assert.False(hostPrepared);
        Assert.False(hostLaunched);
    }

    [Fact]
    public async Task AutomaticHostFallbackDoesNotCreateASessionRuntimeWithoutPackageIdentity()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "unpackaged-app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "fixture").ConfigureAwait(true);
        bool runtimeCreated = false;

        int exitCode = await Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, ["--version"]),
            _ => { },
            _ => Task.FromResult(new NodeRuntime(
                "node.exe",
                new Version(24, 0),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)),
            (_, _, _, _, _, _) => Task.FromResult(17),
            createSessionRuntime: _ =>
            {
                runtimeCreated = true;
                return CreateSessionRuntime();
            },
            probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                null,
                null,
                "backend unavailable",
                MxcHostSupport.Unsupported,
                null,
                MxcSupportEvidence.HostBuild)),
            getPackageFamilyName: () => null,
            readEnvironmentVariable: _ => null);

        Assert.False(runtimeCreated);
        Assert.Equal(17, exitCode);
    }

    [Fact]
    public void SavedSessionWithoutSetupExplainsTheRequiredRecoveryCommand()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        new SessionStateStore(runtime.Paths.SessionStatePath).Write(new SessionRecord
        {
            ApplicationId = runtime.ApplicationId,
            SandboxId = "iso:saved",
            WorkspacePath = Path.Combine(_testDirectory, "workspace"),
            Generation = "test-generation",
            CreatedUtc = DateTimeOffset.UtcNow
        });

        SessionException exception = Assert.Throws<SessionException>(
            runtime.ValidateSavedOwnershipForHostFallback);

        Assert.Contains("clawctl setup", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentRefusesForeignSessionRecordWithoutHostFallback()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        var directLaunches = new List<string>();
        SessionRecord record = new SessionStateStore(runtime.Paths.SessionStatePath)
            .Read(runtime.ApplicationId).Record!;
        new SessionStateStore(runtime.Paths.SessionStatePath).Write(record with
        {
            SandboxId = SandboxIdFor("PFN:Some.Other.App_abc123")
        });

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentWithDirectLaunchProbeAsync(runtime, directLaunches));

        Assert.Contains("Some.Other.App", failure.Message, StringComparison.Ordinal);
        Assert.Empty(directLaunches);
    }

    [Fact]
    public async Task AutomaticDirectRoutingValidatesExistingOwnershipFirst()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        var directLaunches = new List<string>();
        SessionRecord record = new SessionStateStore(runtime.Paths.SessionStatePath)
            .Read(runtime.ApplicationId).Record!;
        new SessionStateStore(runtime.Paths.SessionStatePath).Write(record with
        {
            SandboxId = SandboxIdFor("PFN:Some.Other.App_abc123")
        });

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => Program.RunAgentAsync(
                CreateAgentOptions(),
                _ => { },
                _ => throw new InvalidOperationException("Host Node must not be resolved."),
                (_, _, _, _, _, _) =>
                {
                    directLaunches.Add("direct");
                    return Task.FromResult(0);
                },
                _ => runtime,
                probeReadiness: _ => Task.FromResult(UnavailableReadiness()),
                getPackageFamilyName: () => "OpenClaw.Gateway_test"));

        Assert.Contains("Some.Other.App", failure.Message, StringComparison.Ordinal);
        Assert.Empty(directLaunches);
    }

    [Fact]
    public async Task AgentRefusesSetupSessionMismatchWithoutHostFallback()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        var directLaunches = new List<string>();
        SetupRecord setup = runtime.SetupState.Read(runtime.ApplicationId).Record!;
        runtime.SetupState.Write(setup with { SandboxId = "iso:different" });

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentWithDirectLaunchProbeAsync(runtime, directLaunches));

        Assert.Contains("different session", failure.Message, StringComparison.Ordinal);
        Assert.Empty(directLaunches);
    }

    [Fact]
    public async Task AgentRefusesUnreadableSessionRecordWithoutHostFallback()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        var directLaunches = new List<string>();
        await File.WriteAllTextAsync(runtime.Paths.SessionStatePath, "{ not json");

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentWithDirectLaunchProbeAsync(runtime, directLaunches));

        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
        Assert.Contains("clawctl setup", failure.Message, StringComparison.Ordinal);
        Assert.Empty(directLaunches);
    }

    [Fact]
    public async Task AgentFallsBackToHostWhenSessionRuntimeIsUnavailable()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        _lastSessionBackend!.StartFailure = new MxcException(
            MxcErrorCode.RuntimeUnavailable,
            "The MXC runtime is not available.");
        bool directLaunchAttempted = false;
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        string archivePath = Path.Combine(_testDirectory, "node-v24.15.0-win-x64.zip");

        int exitCode = await Program.RunAgentAsync(
            new HostOptions(applicationDirectory, archivePath, ["--version"]),
            _ => { },
            _ => Task.FromResult(new NodeRuntime(
                "node.exe",
                new Version(24, 15, 0),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)),
            (_, _, _, _, _, _) =>
            {
                directLaunchAttempted = true;
                return Task.FromResult(17);
            },
            _ => runtime);

        Assert.True(directLaunchAttempted);
        Assert.Equal(17, exitCode);
    }

    [Fact]
    public async Task TeardownRequiresForceBeforeRemovingTheSession()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        using var error = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []),
            ["teardown"],
            _ => { },
            TextWriter.Null,
            error,
            installationLifecycle: new FailingFreshLifecycle(runtime));

        Assert.Equal(1, exitCode);
        Assert.Contains("--force", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--no-isolation", null)]
    [InlineData(null, "0")]
    public async Task FreshSetupRejectsDisabledIsolationBeforePreparingTheHostRuntime(
        string? option,
        string? sessionMode)
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        var lifecycle = new FailingFreshLifecycle(CreateSessionRuntime());
        using var output = new StringWriter();
        string[] arguments = option is null
            ? ["setup", "--fresh"]
            : ["setup", "--fresh", option];

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            arguments,
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle,
            readEnvironmentVariable: name =>
                name == SessionRoutingPolicy.ModeVariable ? sessionMode : null);

        Assert.Equal(1, exitCode);
        Assert.Empty(lifecycle.Calls);
        Assert.Contains("--fresh requires isolated-session provisioning", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshSetupStopsBeforeClearingStateWhenTeardownFails()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), "fixture");
        var lifecycle = new FailingFreshLifecycle(CreateSessionRuntime());
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.Equal(1, exitCode);
        Assert.Equal(["validate", "lock", "recovery", "gateway", "session"], lifecycle.Calls);
        Assert.False(lifecycle.Cleaner.Cleared);
        Assert.Contains("Pre-reset diagnostic report:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("teardown is incomplete", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshSetupResetsInOrderBeforeProvisioningAndRecordsConsistentNewState()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime) { TeardownSucceeds = true };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.True(exitCode == 0, output.ToString());
        Assert.Equal(
            ["validate", "lock", "recovery", "gateway", "session", "clean"],
            lifecycle.Calls);
        Assert.Equal(SetupPhase.Ready, runtime.SetupState.Read(runtime.ApplicationId).Record!.Phase);
        Assert.NotNull(runtime.Coordinator.GetRecordedStatus().Record);
        Assert.Contains(
            ((FakeMxcSessionClient)runtime.Backend).Calls,
            call => call.StartsWith("execute:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            ((FakeMxcSessionClient)runtime.Backend).Calls,
            call => call.StartsWith("execute-attached:", StringComparison.Ordinal));
        Assert.Contains("OpenClaw isolated session is ready.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshSetupCleanerFailurePreventsProvisionAndReady()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        var lifecycle = new FailingFreshLifecycle(CreateSessionRuntime())
        {
            TeardownSucceeds = true,
            CleanerException = new IOException("state file is locked")
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh"], _ => { }, output, TextWriter.Null, installationLifecycle: lifecycle);

        Assert.Equal(1, exitCode);
        Assert.Equal(["validate", "lock", "recovery", "gateway", "session", "clean"], lifecycle.Calls);
        Assert.DoesNotContain(
            "OpenClaw isolated session is ready.",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForcedFreshSetupContinuesAfterUnresolvedOwnedCleanupAndKeepsTheWarning()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownResult = new TeardownResult(
                Succeeded: false,
                Message: "MXC backend is unavailable.",
                Detail: "The owned sandbox record could not be verified.")
        };
        List<string> diagnostics = [];
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh", "--force"],
            diagnostics.Add,
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.True(exitCode == 0, output.ToString());
        Assert.Equal(["validate", "lock", "recovery", "gateway", "session", "clean"], lifecycle.Calls);
        Assert.True(lifecycle.Cleaner.Cleared);
        Assert.Equal(SetupPhase.Ready, runtime.SetupState.Read(runtime.ApplicationId).Record!.Phase);
        Assert.Contains(
            "did not prove a pristine machine",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            diagnostics,
            text => text.Contains("did not prove a pristine machine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForcedFreshSetupContinuesWhenTeardownThrowsARecoverableFailure()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownException = new SessionStateException(
                SessionStateFault.Unreadable,
                "session.json cannot be read")
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh", "--force"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.True(exitCode == 0, output.ToString());
        Assert.True(lifecycle.Cleaner.Cleared);
        Assert.Contains(
            "session.json cannot be read",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "cannot remove resources whose ownership record was cleared",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshResetReportPreservesReadableResidualIdentityOutsideClearedState()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        const string oldSandboxId = "iso:residual-session";
        new SessionStateStore(runtime.Paths.SessionStatePath).Write(new SessionRecord
        {
            SchemaVersion = SessionStateStore.CurrentSchemaVersion,
            SandboxId = oldSandboxId,
            ApplicationId = runtime.ApplicationId,
            AgentUserName = "agent_old",
            AgentUserSid = "S-1-5-21-0-0-0-1010",
            WorkspacePath = Path.Combine(_testDirectory, "old-workspace"),
            Generation = "test-generation",
            CreatedUtc = DateTimeOffset.UtcNow
        });
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownResult = new TeardownResult(false, "Backend unavailable.")
        };
        lifecycle.Cleaner.Cleanup = () => File.Delete(runtime.Paths.SessionStatePath);
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh", "--force"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.True(exitCode == 0, output.ToString());
        string reportLine = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Pre-reset diagnostic report:", StringComparison.Ordinal));
        string reportPath = reportLine["Pre-reset diagnostic report:".Length..].Trim();
        try
        {
            string report = await File.ReadAllTextAsync(reportPath);
            Assert.Contains($"sessionSandboxId={oldSandboxId}", report, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task ForcedFreshSetupRecreatesDiagnosticsWithTheResidualWarning()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        string baseDirectory = Path.Combine(_testDirectory, "base");
        string applicationDirectory = Path.Combine(baseDirectory, "app");
        string runtimeDirectory = Path.Combine(baseDirectory, "runtime");
        Directory.CreateDirectory(applicationDirectory);
        Directory.CreateDirectory(runtimeDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), "fixture");
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDirectory, "node-v24.20.0-win-x64.zip"),
            "fixture");
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownResult = new TeardownResult(false, "Owned backend cleanup remains unresolved.")
        };
        lifecycle.Cleaner.Cleanup = () => Directory.Delete(runtime.Paths.StateRoot, recursive: true);
        string logPath = Path.Combine(runtime.Paths.StateRoot, "Logs", "openclaw.log");
        using var diagnostics = HostDiagnosticLog.Create(logPath);
        diagnostics.Write("Host started through the clawctl entrypoint.");
        using var output = new StringWriter();
        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => diagnostics,
            BaseDirectory = baseDirectory,
            Output = output,
            Error = TextWriter.Null,
            InstallationLifecycle = lifecycle
        };

        int exitCode = await Program.RunAsync(["setup", "--fresh", "--force"], startup);

        Assert.True(exitCode == 0, output.ToString());
        string diagnosticsText = await File.ReadAllTextAsync(logPath);
        Assert.Contains("did not prove a pristine machine", diagnosticsText, StringComparison.Ordinal);
        Assert.DoesNotContain("Host started through", diagnosticsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForcedFreshSetupStillStopsWhenLocalCleanupFails()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownResult = new TeardownResult(false, "Owned backend cleanup could not be confirmed."),
            CleanerException = new UnauthorizedAccessException("state root is denied")
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh", "--force"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.Equal(1, exitCode);
        Assert.Equal(["validate", "lock", "recovery", "gateway", "session", "clean"], lifecycle.Calls);
        Assert.Null(runtime.SetupState.Read(runtime.ApplicationId).Record);
        Assert.DoesNotContain("isolated session is ready", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForcedFreshSetupCancellationDoesNotClearOrProvision()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownException = new OperationCanceledException()
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh", "--force"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.Equal(1, exitCode);
        Assert.False(lifecycle.Cleaner.Cleared);
        Assert.Null(runtime.SetupState.Read(runtime.ApplicationId).Record);
        Assert.Contains("cancelled", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FreshSetupProvisionFailureDoesNotClaimReady()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        ((FakeMxcSessionClient)runtime.Backend).ExecuteBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                ((FakeMxcSessionClient)runtime.Backend).Metadata!.EphemeralWorkspacePath,
                "runtime-*.json").Single();
            SessionRuntimeInstallRequest request = SessionRuntimeProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionRuntimeProtocol.SerializeResult(new SessionRuntimeInstallResult
                {
                    RequestId = request.RequestId,
                    Error = "installer failed"
                }));
            return Task.FromResult(new MxcExecutionResult(1, string.Empty, string.Empty));
        };
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownSucceeds = true
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh"], _ => { }, output, TextWriter.Null, installationLifecycle: lifecycle);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            ["validate", "lock", "recovery", "gateway", "session", "clean"],
            lifecycle.Calls);
        Assert.DoesNotContain("OpenClaw isolated session is ready.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentFreshSetupReportsBusyWithoutStartingAnotherReset()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        FakeMxcSessionClient backend = (FakeMxcSessionClient)runtime.Backend;
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownSucceeds = true,
        };
        backend.ExecuteBehavior = async _ =>
        {
            lifecycle.ProvisionStarted.TrySetResult();
            await lifecycle.AllowProvision.Task.ConfigureAwait(false);
            WriteRuntimeInstallResult(backend, 0);
            return new MxcExecutionResult(0, string.Empty, string.Empty);
        };
        var firstOutput = new StringWriter();
        Task<int> first = Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh"], _ => { }, firstOutput, TextWriter.Null, installationLifecycle: lifecycle);
        await lifecycle.ProvisionStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(true);
        using var secondOutput = new StringWriter();

        int second = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh"], _ => { }, secondOutput, TextWriter.Null, installationLifecycle: lifecycle);
        lifecycle.AllowProvision.TrySetResult();

        Assert.Equal(0, await first.ConfigureAwait(true));
        firstOutput.Dispose();
        Assert.Equal(1, second);
        Assert.Equal(1, lifecycle.TeardownCount);
        Assert.Contains("Another OpenClaw process", secondOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupReportsMissingApplicationBeforeResolvingHostNode()
    {
        bool nodeResolutionAttempted = false;

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.RunControlAsync(
            new HostOptions(null, null, []),
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            _ =>
            {
                nodeResolutionAttempted = true;
                return Task.FromResult(
                    new NodeRuntime(
                        "node.exe",
                        new Version(24, 15, 0),
                        System.Runtime.InteropServices.RuntimeInformation
                            .ProcessArchitecture));
            }));

        Assert.False(nodeResolutionAttempted);
    }

    [Fact]
    public async Task AgentReportsMissingApplicationBeforeResolvingHostNode()
    {
        bool nodeResolutionAttempted = false;

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.RunAgentAsync(
            new HostOptions(null, null, []),
            _ => { },
            _ =>
            {
                nodeResolutionAttempted = true;
                return Task.FromResult(
                    new NodeRuntime(
                        "node.exe",
                        new Version(24, 15, 0),
                        System.Runtime.InteropServices.RuntimeInformation
                            .ProcessArchitecture));
            }));

        Assert.False(nodeResolutionAttempted);
    }

    [Fact]
    public async Task AutomaticAgentLaunchUsesHostOnlyWhenReadinessReportsIsolationUnsupported()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        bool resolvedHostNode = false;
        bool launchedHost = false;

        int exitCode = await Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, []),
            _ => { },
            _ =>
            {
                resolvedHostNode = true;
                return Task.FromResult(new NodeRuntime(
                    "node.exe",
                    new Version(24, 0),
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
            },
            (_, _, _, _, _, _) =>
            {
                launchedHost = true;
                return Task.FromResult(17);
            },
            probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                "runtime",
                null,
                null,
                MxcHostSupport.Unsupported,
                null,
                MxcSupportEvidence.HostBuild)),
            getPackageFamilyName: () => "OpenClaw.Gateway_test",
            readEnvironmentVariable: _ => null,
            createSessionRuntime: _ => CreateSessionRuntime());

        Assert.Equal(17, exitCode);
        Assert.True(resolvedHostNode);
        Assert.True(launchedHost);
    }

    [Fact]
    public async Task RequiredAgentLaunchFailsWhenReadinessReportsIsolationUnsupported()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        bool resolvedHostNode = false;

        await Assert.ThrowsAsync<SessionException>(() => Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, []),
            _ => { },
            _ =>
            {
                resolvedHostNode = true;
                return Task.FromResult(new NodeRuntime(
                    "node.exe",
                    new Version(24, 0),
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
            },
            (_, _, _, _, _, _) => Task.FromResult(0),
            probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                "runtime",
                null,
                null,
                MxcHostSupport.Unsupported,
                null,
                MxcSupportEvidence.HostBuild)),
            getPackageFamilyName: () => "OpenClaw.Gateway_test",
            readEnvironmentVariable: name =>
                name == SessionRoutingPolicy.ModeVariable ? "1" : null));

        Assert.False(resolvedHostNode);
    }

    [Fact]
    public async Task SelectedSessionFailureDoesNotResolveOrLaunchHostNode()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        bool resolvedHostNode = false;
        bool launchedHost = false;

        await Assert.ThrowsAsync<SessionException>(() => Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, []),
            _ => { },
            _ =>
            {
                resolvedHostNode = true;
                return Task.FromResult(new NodeRuntime(
                    "node.exe",
                    new Version(24, 0),
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
            },
            (_, _, _, _, _, _) =>
            {
                launchedHost = true;
                return Task.FromResult(0);
            },
            _ => throw new SessionException("setup failed"),
            _ => Task.FromResult(new MxcReadinessReport(
                "runtime",
                null,
                null,
                MxcHostSupport.Supported,
                null,
                MxcSupportEvidence.HostBuild)),
            () => "OpenClaw.Gateway_test",
            _ => null));

        Assert.False(resolvedHostNode);
        Assert.False(launchedHost);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    // Setup refuses to provision on a host that cannot isolate, so every test
    // that drives setup to completion has to state that this one can.
    private static Task<MxcReadinessReport> SupportedHost(CancellationToken _) =>
        Task.FromResult(new MxcReadinessReport(
            "runtime",
            null,
            null,
            MxcHostSupport.Supported,
            null,
            MxcSupportEvidence.HostBuild));

    private static Task<GatewayPersistenceInstallResult> RecoveryConfigured(
        Action<string> _,
        CancellationToken __) =>
        Task.FromResult(new GatewayPersistenceInstallResult(
            GatewayPersistenceState.Ready,
            GatewayPersistenceLane.TaskScheduler,
            "Logon recovery is configured.",
            Changed: true));

    // Teardown removes logon recovery as well as the session, so the test has
    // to supply a scheduler fixture rather than let it reach Task Scheduler.
    private TeardownOrchestrator CreateTeardownOrchestrator(
        SessionRuntime runtime,
        Action<string> log)
    {
        string stateRoot = Path.Combine(_testDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var recovery = new GatewayPersistenceManager(
            new Gateway.FakeGatewayTaskScheduler(),
            new GatewayPersistenceOptions(
                UserSid: "S-1-5-21-1",
                PackageFamilyName: "OpenClaw.Gateway_test",
                LauncherPath: Path.Combine(stateRoot, "gateway-launcher.cmd"),
                StartupFolderPath: Path.Combine(stateRoot, "startup"),
                WorkingDirectory: stateRoot,
                CommandProcessorPath: @"C:\Windows\System32\cmd.exe"),
            log);
        var controller = new GatewayController(
            runtime.Coordinator,
            new SessionGatewayClient(runtime.Backend, log),
            runtime.GatewayState,
            _ => throw new InvalidOperationException(
                "Teardown must never start the gateway."),
            log,
            runtime.RequireSetup,
            runtime.LifecycleLock);

        return new TeardownOrchestrator(
            runtime.LifecycleLock,
            recovery,
            controller,
            runtime.Coordinator,
            runtime.GatewayState,
            new GatewayConfigurationStore(Path.Combine(stateRoot, "gateway-config.json")),
            runtime.SetupState);
    }

    private SessionRuntime CreateSessionRuntime()
    {
        string stateRoot = Path.Combine(_testDirectory, "state");
        string baseDirectory = Path.Combine(_testDirectory, "base");
        Directory.CreateDirectory(baseDirectory);
        string workspace = Path.Combine(_testDirectory, "workspace");
        Directory.CreateDirectory(workspace);
        string helperPath = SessionRuntime.ResolveHelperPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, "fixture");
        var backend = new FakeMxcSessionClient
        {
            Metadata = new MxcProvisionMetadata(
                "agent_1",
                "S-1-5-21-0-0-0-1001",
                workspace)
        };
        backend.ExecuteBehavior = _ =>
        {
            WriteRuntimeInstallResult(backend, 0);
            return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
        };
        _lastSessionBackend = backend;
        return SessionRuntime.Create(
            HostPaths.ForRoot(stateRoot, "OpenClaw.Gateway_test"),
            () => throw new InvalidOperationException("The test backend must be supplied."),
            baseDirectory,
            _ => { },
            backend);
    }

    private async Task<SessionRuntime> SetUpSessionAsync()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        string archivePath = Path.Combine(_testDirectory, "node-v24.15.0-win-x64.zip");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');").ConfigureAwait(false);
        await File.WriteAllTextAsync(archivePath, "fixture").ConfigureAwait(false);
        int exitCode = await Program.RunControlAsync(
            new HostOptions(applicationDirectory, archivePath, []),
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            _ => Task.FromResult(new NodeRuntime(
                "node.exe",
                new Version(24, 15, 0),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)),
            new StubbedRecoveryLifecycle(runtime)).ConfigureAwait(false);

        Assert.Equal(0, exitCode);
        return runtime;
    }

    // Setup only reaches Ready once logon recovery is configured, and a test
    // must never register a real scheduled task. Everything else is left to
    // production behaviour so the fixture still exercises the real path.
    private sealed class StubbedRecoveryLifecycle(SessionRuntime runtime)
        : IInstallationLifecycle
    {
        private static readonly InstallationLifecycle Inner =
            InstallationLifecycle.Production;

        public SessionRuntime CreateRuntime(Action<string> log) => runtime;

        public Task<SessionRoutingDecision> CheckSessionSupportAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionRoutingDecision(
                SessionRouting.Session,
                "Test session support is available."));

        public NodeRuntime PrepareHostRuntime(HostOptions options, Action<string> log) =>
            Inner.PrepareHostRuntime(options, log);

        public PackageRuntimeMetadata ValidatePackageRuntime(
            HostOptions options, SessionRuntime sessionRuntime) =>
            Inner.ValidatePackageRuntime(options, sessionRuntime);

        public ISessionLockHandle AcquireLifecycleLock(SessionRuntime sessionRuntime) =>
            Inner.AcquireLifecycleLock(sessionRuntime);

        public Task<TeardownResult> TeardownAsync(
            HostOptions options,
            SessionRuntime sessionRuntime,
            Action<string> log,
            bool lockAlreadyHeld,
            CancellationToken cancellationToken) =>
            Inner.TeardownAsync(options, sessionRuntime, log, lockAlreadyHeld, cancellationToken);

        public IInstallationStateCleaner CreateStateCleaner(SessionRuntime sessionRuntime) =>
            Inner.CreateStateCleaner(sessionRuntime);

        public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.",
                Changed: true));
    }

    private static string SandboxIdFor(string applicationId) =>
        $"iso:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{{\"appId\":\"{applicationId}\"}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    private static MxcReadinessReport UnavailableReadiness() =>
        new(
            RuntimeDirectory: null,
            Provenance: null,
            RuntimeUnavailableReason: "The runtime is unavailable.",
            HostSupport: MxcHostSupport.Supported,
            HostBuild: null,
            SupportEvidence: MxcSupportEvidence.BackendProbe,
            BackendProbe: new MxcBackendProbe(false, "base-container", []),
            BackendProbeFailureReason: null);

    private Task<int> RunAgentWithDirectLaunchProbeAsync(
        SessionRuntime runtime,
        List<string> directLaunches)
    {
        ArgumentNullException.ThrowIfNull(directLaunches);

        return Program.RunAgentAsync(
            CreateAgentOptions(),
            _ => { },
            _ => throw new InvalidOperationException("Host Node must not be resolved."),
            (_, _, _, _, _, _) =>
            {
                directLaunches.Add("direct");
                return Task.FromResult(0);
            },
            _ => runtime,
            probeReadiness: _ => Task.FromResult(UnavailableReadiness()),
            getPackageFamilyName: () => "OpenClaw.Gateway_test");
    }

    private static void WriteRuntimeInstallResult(FakeMxcSessionClient backend, int exitCode)
    {
        string requestPath = Directory.GetFiles(backend.Metadata!.EphemeralWorkspacePath, "runtime-*.json")
            .Single();
        SessionRuntimeInstallRequest request = SessionRuntimeProtocol.ReadRequest(
            File.ReadAllText(requestPath));
        File.WriteAllText(
            SessionLaunchProtocol.ResultPathFor(requestPath),
            SessionRuntimeProtocol.SerializeResult(new SessionRuntimeInstallResult
            {
                RequestId = request.RequestId,
                ExecutablePath = @"C:\Users\agent_1\AppData\Local\OpenClawGatewayMSIX\agent-node\node.exe",
                Version = "24.20.0",
                ArchiveName = "node-v24.20.0-win-x64.zip",
                Error = exitCode == 0 ? null : "installer failed"
            }));
    }

    private HostOptions CreateAgentOptions()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "agent-app");
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        return new HostOptions(applicationDirectory, null, ["--version"]);
    }

    private async Task<string> CreateApplicationAsync()
    {
        string applicationDirectory = Path.Combine(_testDirectory, Guid.NewGuid().ToString("N"), "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), "fixture")
            .ConfigureAwait(false);
        return applicationDirectory;
    }

    private HostOptions CreateSetupOptions(string applicationDirectory)
    {
        string archivePath = Path.Combine(_testDirectory, "node-v24.20.0-win-x64.zip");
        File.WriteAllText(archivePath, "fixture");
        return new HostOptions(applicationDirectory, archivePath, []);
    }

    private sealed class FailingFreshLifecycle : IInstallationLifecycle
    {
        private readonly SessionRuntime _runtime;

        public FailingFreshLifecycle(SessionRuntime runtime)
        {
            _runtime = runtime;
        }

        public List<string> Calls { get; } = [];

        public RecordingCleaner Cleaner { get; } = new();

        public Exception? CleanerException { get; init; }

        public Exception? TeardownException { get; init; }

        public TeardownResult? TeardownResult { get; init; }

        public bool TeardownSucceeds { get; init; }

        public TaskCompletionSource ProvisionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowProvision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TeardownCount { get; private set; }

        public SessionRuntime CreateRuntime(Action<string> log) => _runtime;

        public Task<SessionRoutingDecision> CheckSessionSupportAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionRoutingDecision(
                SessionRouting.Session,
                "The isolated-session runtime is available."));

        public NodeRuntime PrepareHostRuntime(HostOptions options, Action<string> log) =>
            new(
                "node.exe",
                new Version(24, 20, 0),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

        public PackageRuntimeMetadata ValidatePackageRuntime(
            HostOptions options,
            SessionRuntime runtime)
        {
            Calls.Add("validate");
            return new PackageRuntimeMetadata("node.zip", new Version(24, 0), runtime.HelperPath);
        }

        public Task<TeardownResult> TeardownAsync(
            HostOptions options,
            SessionRuntime runtime,
            Action<string> log,
            bool lockAlreadyHeld,
            CancellationToken cancellationToken)
        {
            Calls.Add("recovery");
            Calls.Add("gateway");
            Calls.Add("session");
            TeardownCount++;
            Assert.True(lockAlreadyHeld);
            if (TeardownException is not null)
            {
                throw TeardownException;
            }

            if (TeardownResult is not null)
            {
                return Task.FromResult(TeardownResult);
            }

            return Task.FromResult(TeardownSucceeds
                ? new TeardownResult(Succeeded: true, Message: "Removed prior session.")
                : new TeardownResult(Succeeded: false, Message: "Recovery removal failed."));
        }

        public ISessionLockHandle AcquireLifecycleLock(SessionRuntime runtime)
        {
            Calls.Add("lock");
            if (Calls.Count(static call => call == "lock") > 1)
            {
                throw new SessionBusyException(TimeSpan.Zero);
            }

            return new RecordingLockHandle();
        }

        public IInstallationStateCleaner CreateStateCleaner(SessionRuntime runtime)
        {
            Calls.Add("clean");
            Cleaner.Exception = CleanerException;
            return Cleaner;
        }

        public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "ready",
                false));
    }

    private sealed class RecordingCleaner : IInstallationStateCleaner
    {
        public bool Cleared { get; private set; }

        public Exception? Exception { get; set; }

        public Action? Cleanup { get; set; }

        public void Clear()
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            Cleared = true;
            Cleanup?.Invoke();
        }

    }

    private sealed class RecordingLockHandle : ISessionLockHandle
    {
        public void Dispose()
        {
        }
    }
}
