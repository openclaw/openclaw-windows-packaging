using System.IO.Compression;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;
using System.Text.Json;

namespace OpenClaw.Launcher.Tests;

public sealed class ProgramTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();
    private FakeMxcSessionClient? _lastSessionBackend;

    [Fact]
    public async Task AgentForwardsEveryArgumentIntoTheSession()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        HostOptions setupOptions = CreateSetupOptions(applicationDirectory);
        SessionRuntime runtime = CreateSessionRuntime();
        int setupExitCode = await Program.RunControlAsync(
            setupOptions,
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            installationLifecycle: new FailingFreshLifecycle(runtime) { TeardownSucceeds = true });
        Assert.Equal(0, setupExitCode);

        string[] arguments = ["gateway", "run", "--port", "12345", "--", "a b"];
        string[]? forwarded = null;
        _lastSessionBackend!.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                "launch-*.json").Single();
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            forwarded = [.. request.Arguments!.Skip(1)];
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = request.RequestId,
                    Launched = true,
                    ExitCode = 7,
                }));
            return Task.FromResult(0);
        };
        _lastSessionBackend.ExecuteBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                "config-readiness-*.json").Single();
            SessionConfigReadinessRequest request =
                SessionConfigReadinessProtocol.ReadRequest(
                    File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionConfigReadinessProtocol.SerializeResult(
                    new SessionConfigReadinessResult
                    {
                        RequestId = request.RequestId,
                        State = SessionConfigReadinessState.StartupEligible,
                        Reason = SessionConfigReadinessReason.GatewayModeLocal
                    }));
            return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
        };
        var error = new StringWriter();

        int exitCode = await Program.RunAgentAsync(
            new HostOptions(
                applicationDirectory,
                setupOptions.PackagedNodeArchivePath,
                arguments),
            _ => { },
            _ => runtime,
            probeReadiness: SupportedHost,
            getPackageFamilyName: () => runtime.Paths.PackageFamilyName,
            readEnvironmentVariable: _ => null,
            isInteractive: () => true,
            error: error,
            getLogonSessionId: () => "logon-a");

        Assert.Equal(arguments, forwarded);
        Assert.Equal(7, exitCode);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task SuccessfulInteractiveAgentHintsWhenEligibleGatewayWasNeverStarted()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        HostOptions setupOptions = CreateSetupOptions(applicationDirectory);
        SessionRuntime runtime = CreateSessionRuntime();
        int setupExitCode = await Program.RunControlAsync(
            setupOptions,
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            installationLifecycle: new FailingFreshLifecycle(runtime) { TeardownSucceeds = true });
        Assert.Equal(0, setupExitCode);

        _lastSessionBackend!.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                "launch-*.json").Single();
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = request.RequestId,
                    Launched = true,
                    ExitCode = 0,
                }));
            return Task.FromResult(0);
        };
        _lastSessionBackend.ExecuteBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                "config-readiness-*.json").Single();
            SessionConfigReadinessRequest request =
                SessionConfigReadinessProtocol.ReadRequest(
                    File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionConfigReadinessProtocol.SerializeResult(
                    new SessionConfigReadinessResult
                    {
                        RequestId = request.RequestId,
                        State = SessionConfigReadinessState.StartupEligible,
                        Reason = SessionConfigReadinessReason.GatewayModeLocal
                    }));
            return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
        };
        var error = new StringWriter();

        int exitCode = await Program.RunAgentAsync(
            new HostOptions(
                applicationDirectory,
                setupOptions.PackagedNodeArchivePath,
                ["status"]),
            _ => { },
            _ => runtime,
            probeReadiness: SupportedHost,
            getPackageFamilyName: () => runtime.Paths.PackageFamilyName,
            readEnvironmentVariable: _ => null,
            isInteractive: () => true,
            error: error,
            getLogonSessionId: () => "logon-a",
            errorIsProcessConsoleWriter: () => true,
            errorIsInteractive: () => false,
            supportsUnicode: () => true);

        Assert.Equal(0, exitCode);
        Assert.Contains("\u001b[", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("\U0001f980", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            AgentGatewayGuidance.Hint,
            System.Text.RegularExpressions.Regex.Replace(
                error.ToString(),
                "\u001b\\[[0-9;]*m",
                string.Empty),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task BackendAdvisoryFailurePreservesAgentExitCode(int childExitCode)
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        HostOptions setupOptions = CreateSetupOptions(applicationDirectory);
        SessionRuntime runtime = CreateSessionRuntime();
        int setupExitCode = await Program.RunControlAsync(
            setupOptions,
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            installationLifecycle: new FailingFreshLifecycle(runtime) { TeardownSucceeds = true });
        Assert.Equal(0, setupExitCode);

        FakeMxcSessionClient backend = _lastSessionBackend!;
        backend.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                backend.Metadata!.EphemeralWorkspacePath,
                "launch-*.json").Single();
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = request.RequestId,
                    Launched = true,
                    ExitCode = childExitCode,
                }));
            backend.ExecuteFailure =
                new MxcException(MxcErrorCode.BackendError, "readiness dispatch failed");
            return Task.FromResult(0);
        };
        var log = new List<string>();

        int exitCode = await Program.RunAgentAsync(
            new HostOptions(
                applicationDirectory,
                setupOptions.PackagedNodeArchivePath,
                ["status"]),
            log.Add,
            _ => runtime,
            probeReadiness: SupportedHost,
            getPackageFamilyName: () => runtime.Paths.PackageFamilyName,
            readEnvironmentVariable: _ => null,
            isInteractive: () => true,
            error: TextWriter.Null,
            getLogonSessionId: () => "logon-advisory");

        Assert.Equal(childExitCode, exitCode);
        Assert.Contains(
            log,
            line => line.Contains(
                "MxcException: readiness dispatch failed",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManualGatewayStartAcknowledgesBeforeAStartFailure()
    {
        SessionRuntime runtime = await SetUpSessionAsync().ConfigureAwait(true);
        ((FakeMxcSessionClient)runtime.Backend).ExecuteFailure =
            new SessionException("gateway start failed");

        await Assert.ThrowsAsync<SessionException>(
            () => Program.RunControlAsync(
                CreateSetupOptions(Path.Combine(_testDirectory, "app")),
                ["gateway-service", "start"],
                _ => { },
                TextWriter.Null,
                TextWriter.Null,
                installationLifecycle: new FailingFreshLifecycle(runtime),
                getLogonSessionId: () => "logon-manual"));

        var store = new GatewayGuidanceStateStore(
            runtime.Paths.GatewayGuidanceStatePath);
        Assert.True(store.IsAcknowledged("logon-manual"));
    }

    [Fact]
    public async Task RetainedRecoveryScriptUpgradesWithoutManualAcknowledgement()
    {
        SessionRuntime runtime = await SetUpSessionAsync().ConfigureAwait(true);
        string activationScriptPath =
            Path.ChangeExtension(runtime.Paths.GatewayLauncherPath, ".ps1");
        string legacyScript =
            GatewayLauncherScript.CreateActivationScript(
                runtime.Paths.PackageFamilyName!)
            .Replace(
                GatewayLauncherScript.ControlArguments,
                "gateway-service start",
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            activationScriptPath,
            legacyScript,
            CancellationToken.None);
        ((FakeMxcSessionClient)runtime.Backend).ExecuteFailure =
            new SessionException("gateway recovery failed");

        await Assert.ThrowsAsync<SessionException>(
            () => Program.RunControlAsync(
                CreateSetupOptions(Path.Combine(_testDirectory, "app")),
                ["gateway-service", "start"],
                _ => { },
                TextWriter.Null,
                TextWriter.Null,
                installationLifecycle: new FailingFreshLifecycle(runtime),
                getLogonSessionId: () => "logon-recovery"));

        var store = new GatewayGuidanceStateStore(
            runtime.Paths.GatewayGuidanceStatePath);
        Assert.False(store.IsAcknowledged("logon-recovery"));
        Assert.Contains(
            GatewayLauncherScript.ControlArguments,
            await File.ReadAllTextAsync(
                activationScriptPath,
                CancellationToken.None),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The host never composes the agent's <c>NODE_OPTIONS</c>. It names the
    /// preload, and the guest appends it to whatever the agent already set, so
    /// settings such as <c>--max-old-space-size</c> survive setup and the
    /// host's own value never reaches the agent.
    /// </summary>
    [Fact]
    public async Task AgentLaunchNamesTheNativeRedirectWithoutSettingNodeOptions()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        HostOptions setupOptions = CreateSetupOptions(applicationDirectory);
        SessionRuntime runtime = CreateSessionRuntime();
        int setupExitCode = await Program.RunControlAsync(
            setupOptions,
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            installationLifecycle: new FailingFreshLifecycle(runtime) { TeardownSucceeds = true });
        Assert.Equal(0, setupExitCode);

        // Stands in for a setup that staged native packages.
        string nativeRoot = Path.Combine(_testDirectory, "agent-native", "0123456789abcdef");
        Directory.CreateDirectory(nativeRoot);
        SetupRecord staged = runtime.SetupState.Read(runtime.ApplicationId).Record!;
        runtime.SetupState.Write(staged with { AgentNativeRoot = nativeRoot });

        SessionLaunchRequest? launched = null;
        _lastSessionBackend!.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                "launch-*.json").Single();
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            launched = request;

            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = request.RequestId,
                    Launched = true,
                    ExitCode = 0,
                }));
            return Task.FromResult(0);
        };

        await Program.RunAgentAsync(
            new HostOptions(
                applicationDirectory,
                setupOptions.PackagedNodeArchivePath,
                ["doctor"]),
            _ => { },
            _ => runtime,
            probeReadiness: SupportedHost,
            getPackageFamilyName: () => runtime.Paths.PackageFamilyName,
            readEnvironmentVariable: name =>
                name == OpenClawRuntimeEnvironment.NodeOptionsVariable
                    ? "--host-only-flag"
                    : null).ConfigureAwait(true);

        Assert.NotNull(launched);
        Assert.Contains(
            OpenClawRuntimeEnvironment.NativeRedirectFileName,
            launched.NodeOptionsSuffix,
            StringComparison.Ordinal);
        Assert.Equal(nativeRoot, launched.NativeRootPath);

        // The assigned environment must not carry NODE_OPTIONS at all: it
        // would replace the agent's, and the host's value is not the agent's.
        Assert.False(
            launched.Environment!.ContainsKey(
                OpenClawRuntimeEnvironment.NodeOptionsVariable));
    }

    [Fact]
    public async Task AgentControlCExitsSilentlyWithPortableInterruptedCode()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        HostOptions setupOptions = CreateSetupOptions(applicationDirectory);
        SessionRuntime runtime = CreateSessionRuntime();
        int setupExitCode = await Program.RunControlAsync(
            setupOptions,
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            installationLifecycle: new FailingFreshLifecycle(runtime) { TeardownSucceeds = true });
        Assert.Equal(0, setupExitCode);
        _lastSessionBackend!.AttachedBehavior = _ =>
            Task.FromResult(unchecked((int)0xc000013a));

        int exitCode = await Program.RunAgentAsync(
            new HostOptions(applicationDirectory, setupOptions.PackagedNodeArchivePath, ["status"]),
            _ => { },
            _ => runtime,
            probeReadiness: SupportedHost,
            getPackageFamilyName: () => runtime.Paths.PackageFamilyName,
            readEnvironmentVariable: _ => null);

        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task PowerShellControlCExitsSilentlyWithPortableInterruptedCode()
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
        _lastSessionBackend!.ExecuteBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                "tools-*.json").Single();
            SessionToolInstallRequest request = SessionRuntimeProtocol.ReadToolInstallRequest(
                File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionRuntimeProtocol.SerializeToolInstallResult(new SessionToolInstallResult
                {
                    RequestId = request.RequestId,
                    ShimPath = @"C:\Users\agent\Shared\.openclaw-tools\openclaw.cmd"
                }));
            return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
        };
        _lastSessionBackend!.AttachedBehavior = _ =>
            Task.FromResult(unchecked((int)0xc000013a));
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            options,
            ["pwsh"],
            _ => { },
            output,
            error,
            installationLifecycle: lifecycle);

        Assert.Equal(130, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task AgentFailsLoudlyWhenTheMachineCannotHostASession()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        bool runtimeCreated = false;

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => Program.RunAgentAsync(
                new HostOptions(applicationDirectory, null, ["--version"]),
                _ => { },
                _ =>
                {
                    runtimeCreated = true;
                    return CreateSessionRuntime();
                },
                probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                    "runtime",
                    null,
                    null,
                    MxcHostSupport.Unsupported,
                    null,
                    MxcSupportEvidence.HostBuild)),
                getPackageFamilyName: () => "OpenClaw.Gateway_test",
                readEnvironmentVariable: _ => null));

        Assert.False(runtimeCreated);
        AssertRecommendsNewerWindows(failure.Message);
    }

    [Fact]
    public async Task SetupFailsLoudlyWhenTheMachineCannotHostASession()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        string entryPoint = Path.Combine(applicationDirectory, "openclaw.mjs");
        await File.WriteAllTextAsync(entryPoint, "console.log('fixture');");
        DateTime lastWriteTime = File.GetLastWriteTimeUtc(entryPoint);
        var options = new HostOptions(applicationDirectory, null, []);
        using var output = new StringWriter();

        // The test host is unpackaged, so production readiness reports a
        // machine that cannot own a session.
        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => Program.RunControlAsync(
                options,
                ["setup"],
                _ => { },
                output,
                TextWriter.Null));

        AssertRecommendsNewerWindows(failure.Message);
        Assert.True(File.Exists(entryPoint));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(entryPoint));

        // The attempted requirement check is visible, but nothing is reported
        // as ready on a machine that cannot host a session.
        Assert.Contains(
            "Checking isolated-session support.",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("ready", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupJsonFailureWritesOnlyTheVersionedErrorDocument()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(applicationDirectory, null, []),
            ["setup", "--json"],
            _ => { },
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Empty(error.ToString());
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("setup", root.GetProperty("command").GetString());
        Assert.Equal("cli_error", root.GetProperty("error").GetProperty("type").GetString());
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
    }

    [Fact]
    public async Task UnsupportedMachineFailsBeforeTouchingSavedSessionState()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        string applicationDirectory = Path.Combine(_testDirectory, "fallback-app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "fixture").ConfigureAwait(true);
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.Paths.SessionStatePath)!);
        await File.WriteAllTextAsync(
            runtime.Paths.SessionStatePath,
            "{ not json").ConfigureAwait(true);

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => Program.RunAgentAsync(
                new HostOptions(applicationDirectory, null, ["--version"]),
                _ => { },
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

        AssertRecommendsNewerWindows(failure.Message);
    }

    [Fact]
    public async Task UnpackagedHostFailsWithoutCreatingASessionRuntime()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "unpackaged-app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "fixture").ConfigureAwait(true);
        bool runtimeCreated = false;

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => Program.RunAgentAsync(
                new HostOptions(applicationDirectory, null, ["--version"]),
                _ => { },
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
                readEnvironmentVariable: _ => null));

        Assert.False(runtimeCreated);
        Assert.Contains("installed package", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentRefusesForeignSessionRecord()
    {
        SessionRuntime runtime = await SetUpSessionAsync();

        SessionRecord record = new SessionStateStore(runtime.Paths.SessionStatePath)
            .Read(runtime.ApplicationId).Record!;
        new SessionStateStore(runtime.Paths.SessionStatePath).Write(record with
        {
            SandboxId = SandboxIdFor("PFN:Some.Other.App_abc123")
        });

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentOnSupportedHostAsync(runtime));

        Assert.Contains("Some.Other.App", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentRefusesSetupSessionMismatch()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        SetupRecord setup = runtime.SetupState.Read(runtime.ApplicationId).Record!;
        runtime.SetupState.Write(setup with { SandboxId = "iso:different" });

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentOnSupportedHostAsync(runtime));

        Assert.Contains("different session", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentRefusesUnreadableSessionRecord()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        await File.WriteAllTextAsync(runtime.Paths.SessionStatePath, "{ not json");

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentOnSupportedHostAsync(runtime));

        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
        Assert.Contains("clawctl setup", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentSurfacesAnUnavailableSessionRuntimeInsteadOfRunningOnTheHost()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        _lastSessionBackend!.StartFailure = new MxcException(
            MxcErrorCode.RuntimeUnavailable,
            "The MXC runtime is not available.");

        SessionCapabilityUnavailableException failure =
            await Assert.ThrowsAsync<SessionCapabilityUnavailableException>(
                () => RunAgentOnSupportedHostAsync(runtime));

        Assert.Contains("not available", failure.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task SetupRejectsTheRemovedNoIsolationOption()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        var lifecycle = new FailingFreshLifecycle(CreateSessionRuntime());
        using var error = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--no-isolation"],
            _ => { },
            TextWriter.Null,
            error,
            installationLifecycle: lifecycle);

        Assert.Equal(1, exitCode);
        Assert.Empty(lifecycle.Calls);
        Assert.Contains("--no-isolation", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupIgnoresTheRemovedSessionOptOutVariable()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime) { TeardownSucceeds = true };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle,
            readEnvironmentVariable: name =>
                name == "OPENCLAW_SESSION" ? "0" : null);

        Assert.Equal(0, exitCode);
        Assert.NotNull(runtime.SetupState.Read(runtime.ApplicationId).Record);
        string rendered = output.ToString();
        Assert.Contains("Checking isolated-session support.", rendered, StringComparison.Ordinal);
        Assert.Contains("Preparing the isolated session.", rendered, StringComparison.Ordinal);
        Assert.Contains(
            "Installing Node.js in the isolated session.",
            rendered,
            StringComparison.Ordinal);
        Assert.Contains("Enabling gateway startup at sign-in.", rendered, StringComparison.Ordinal);
        Assert.Contains("Finalizing setup.", rendered, StringComparison.Ordinal);
        Assert.Contains("openclaw onboard", rendered, StringComparison.Ordinal);
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
        Assert.Contains("Session:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "recovery removal failed",
            output.ToString(),
            StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("Session:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ready", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusReportsSessionRuntimeGatewayAndRecovery()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime);
        using var setupOutput = new StringWriter();
        int setupExitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup"],
            _ => { },
            setupOutput,
            TextWriter.Null,
            installationLifecycle: lifecycle);
        ConfigureConfigReadiness(
            (FakeMxcSessionClient)runtime.Backend,
            SessionConfigReadinessState.StartupEligible,
            SessionConfigReadinessReason.GatewayModeLocal);
        using var statusOutput = new StringWriter();

        int statusExitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["status"],
            _ => { },
            statusOutput,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.Equal(0, setupExitCode);
        Assert.Equal(0, statusExitCode);
        string status = statusOutput.ToString();
        Assert.Contains("Session:", status, StringComparison.Ordinal);
        Assert.Contains("Runtime:", status, StringComparison.Ordinal);
        Assert.Contains("Node.js 24.20.0", status, StringComparison.Ordinal);
        Assert.Contains("Gateway:", status, StringComparison.Ordinal);
        Assert.Contains("not started", status, StringComparison.Ordinal);
        Assert.Contains("Readiness:", status, StringComparison.Ordinal);
        Assert.Contains("startup eligible", status, StringComparison.Ordinal);
        Assert.Contains("Recovery:", status, StringComparison.Ordinal);
        Assert.Contains("configured", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusJsonReportsStructuredSessionGatewayAndRecovery()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime);
        using var setupOutput = new StringWriter();
        int setupExitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup"],
            _ => { },
            setupOutput,
            TextWriter.Null,
            installationLifecycle: lifecycle);
        ConfigureConfigReadiness(
            (FakeMxcSessionClient)runtime.Backend,
            SessionConfigReadinessState.StartupEligible,
            SessionConfigReadinessReason.GatewayModeLocal);
        using var statusOutput = new StringWriter();
        using var statusError = new StringWriter();

        int statusExitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["status", "--json"],
            _ => { },
            statusOutput,
            statusError,
            installationLifecycle: lifecycle);

        Assert.Equal(0, setupExitCode);
        Assert.Equal(0, statusExitCode);
        Assert.Empty(statusError.ToString());
        using JsonDocument document = JsonDocument.Parse(statusOutput.ToString());
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("running", root.GetProperty("session").GetProperty("state").GetString());
        Assert.Equal("24.20.0", root.GetProperty("session").GetProperty("nodeVersion").GetString());
        Assert.Equal("not-started", root.GetProperty("gateway").GetProperty("state").GetString());
        JsonElement readiness =
            root.GetProperty("gateway").GetProperty("readiness");
        Assert.Equal("startup-eligible", readiness.GetProperty("state").GetString());
        Assert.Equal("gateway-mode-local", readiness.GetProperty("reason").GetString());
        Assert.Equal("configured", root.GetProperty("recovery").GetProperty("state").GetString());
    }

    [Fact]
    public async Task GatewayStatusStartsOnlyTheRecordedSessionAndReportsReadiness()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime);
        int setupExitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            installationLifecycle: lifecycle);
        var backend = (FakeMxcSessionClient)runtime.Backend;
        ConfigureConfigReadiness(
            backend,
            SessionConfigReadinessState.NotReady,
            SessionConfigReadinessReason.GatewayModeMissing);
        backend.Calls.Clear();
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["gateway-service", "status"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.Equal(0, setupExitCode);
        Assert.Equal(0, exitCode);
        Assert.Contains(
            backend.Calls,
            call => call.StartsWith("start:", StringComparison.Ordinal));
        Assert.Contains(
            backend.Calls,
            call => call.StartsWith("execute:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            backend.Calls,
            call => call.StartsWith("provision", StringComparison.Ordinal));
        Assert.Contains("Readiness:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("not ready", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("gateway.mode is missing", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "clawctl gateway-service start",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayStatusWithoutARecordedSessionReportsUnavailableWithoutProvisioning()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime);
        var backend = (FakeMxcSessionClient)runtime.Backend;
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(Path.Combine(_testDirectory, "app")),
            ["gateway-service", "status"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.Equal(0, exitCode);
        Assert.Empty(backend.Calls);
        Assert.Contains("Readiness:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unavailable", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StatusJsonRetainsGatewayWhenReadinessProbeFails(
        bool gatewayOnly,
        bool malformedResult)
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime);
        int setupExitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            installationLifecycle: lifecycle);
        var backend = (FakeMxcSessionClient)runtime.Backend;
        if (malformedResult)
        {
            backend.ExecuteBehavior = _ =>
            {
                string requestPath = Directory.GetFiles(
                    backend.Metadata!.EphemeralWorkspacePath,
                    "config-readiness-*.json").Single();
                File.WriteAllText(
                    SessionLaunchProtocol.ResultPathFor(requestPath),
                    SessionConfigReadinessProtocol.SerializeResult(
                        new SessionConfigReadinessResult
                        {
                            RequestId = "different-request",
                            State = SessionConfigReadinessState.StartupEligible,
                            Reason = SessionConfigReadinessReason.GatewayModeLocal
                        }));
                return Task.FromResult(
                    new MxcExecutionResult(0, string.Empty, string.Empty));
            };
        }
        else
        {
            backend.ExecuteFailure =
                new MxcException(
                    MxcErrorCode.BackendError,
                    "readiness backend unavailable");
        }
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            gatewayOnly
                ? ["gateway-service", "status", "--json"]
                : ["status", "--json"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.Equal(0, setupExitCode);
        Assert.Equal(1, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "not-started",
            root.GetProperty("gateway").GetProperty("state").GetString());
        JsonElement readiness =
            root.GetProperty("gateway").GetProperty("readiness");
        Assert.Equal("unknown", readiness.GetProperty("state").GetString());
        Assert.Contains(
            malformedResult
                ? "does not match"
                : "readiness backend unavailable",
            readiness.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayStatusReportsUnknownReadinessWhenRecordedSessionIsUnavailable()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime);
        int setupExitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            installationLifecycle: lifecycle);
        ((FakeMxcSessionClient)runtime.Backend).StartFailure = new MxcException(
            MxcErrorCode.RuntimeUnavailable,
            "MXC is unavailable");
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["gateway-service", "status", "--json"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.Equal(0, setupExitCode);
        Assert.Equal(1, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement readiness = document.RootElement
            .GetProperty("gateway")
            .GetProperty("readiness");
        Assert.Equal("unknown", readiness.GetProperty("state").GetString());
        Assert.Contains(
            "MXC is unavailable",
            readiness.GetProperty("detail").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusJsonFailureRetainsStateDetails()
    {
        using var output = new StringWriter();
        var result = new StatusCommandResult(
            new SessionStatus(
                SessionAvailability.Stale,
                null,
                SessionStateFault.Missing,
                "The recorded session is missing."),
            new GatewayStatusReport(
                GatewayState.Unhealthy,
                null,
                "The gateway is unhealthy."),
            new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.TaskScheduler,
                "Recovery needs attention."),
            null);

        ClawCtlJson.WriteResult(output, result);

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("stale", root.GetProperty("session").GetProperty("state").GetString());
        Assert.Equal("unhealthy", root.GetProperty("gateway").GetProperty("state").GetString());
        Assert.Equal(
            "action-required",
            root.GetProperty("recovery").GetProperty("state").GetString());
        Assert.Equal("cli_error", root.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task TeardownJsonWithoutForceWritesAFailureDocument()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []),
            ["teardown", "--json"],
            _ => { },
            output,
            error,
            installationLifecycle: new FailingFreshLifecycle(CreateSessionRuntime()));

        Assert.Equal(1, exitCode);
        Assert.Empty(error.ToString());
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("teardown", root.GetProperty("command").GetString());
        Assert.Contains(
            "--force",
            root.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);
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
            "not proven clean",
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

        // The warning is rendered inside a note callout, so the renderer owns
        // where it breaks lines. Flatten the box gutters and wrapping before
        // asserting, and assert the whole sentence rather than a fragment that
        // happens to survive a particular line break.
        Assert.Contains(
            "because the record of what we owned is gone.",
            FlattenRenderedText(output.ToString()),
            StringComparison.Ordinal);
    }

    private static string FlattenRenderedText(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value.Replace('|', ' ').Replace('\u2502', ' '),
            @"\s+",
            " ");

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
        string reportPath = runtime.Paths.PreResetReportPath;
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
    public async Task ProductionCleanupPreservesEveryResetReportForCollectLogs()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        const string oldSandboxId = "iso:production-cleaner-residual";
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
        string productStateRoot = Path.Combine(_testDirectory, "product-state");
        Directory.CreateDirectory(productStateRoot);
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownResult = new TeardownResult(false, "Backend unavailable."),
            StateCleaner = new InstallationStateCleaner(runtime.Paths, productStateRoot)
        };
        HostOptions options = CreateSetupOptions(applicationDirectory);
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            options,
            ["setup", "--fresh", "--force"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        Assert.True(exitCode == 0, output.ToString());
        string replacementSandboxId = runtime.SetupState
            .Read(runtime.ApplicationId)
            .Record!
            .SandboxId!;

        var secondLifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownResult = new TeardownResult(false, "Backend unavailable."),
            StateCleaner = new InstallationStateCleaner(runtime.Paths, productStateRoot)
        };
        using var secondOutput = new StringWriter();
        int secondExitCode = await Program.RunControlAsync(
            options,
            ["setup", "--fresh", "--force"],
            _ => { },
            secondOutput,
            TextWriter.Null,
            installationLifecycle: secondLifecycle);

        Assert.True(secondExitCode == 0, secondOutput.ToString());
        ((FakeMxcSessionClient)runtime.Backend).ExecuteBehavior = _ =>
            Task.FromResult(new MxcExecutionResult(1, string.Empty, "guest collection unavailable"));
        string bundlePath = Path.Combine(_testDirectory, "post-reset.zip");
        DiagnosticsBundleResult bundle = await GatewayRuntime.Create(
            options,
            runtime.Paths,
            runtime,
            _ => { })
            .CollectLogsAsync(bundlePath, CancellationToken.None);
        Assert.Equal(bundlePath, bundle.BundlePath);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        ZipArchiveEntry report = Assert.Single(
            archive.Entries,
            entry => entry.FullName == "host/pre-reset.log");
        using var reader = new StreamReader(report.Open());
        string reportText = await reader.ReadToEndAsync();
        Assert.Contains(
            $"sessionSandboxId={oldSandboxId}",
            reportText,
            StringComparison.Ordinal);
        Assert.Contains(
            $"sessionSandboxId={replacementSandboxId}",
            reportText,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            reportText.Split(
                "--- pre-reset snapshot ---",
                StringSplitOptions.None).Length - 1);
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
        string text = FlattenRenderedText(output.ToString());
        Assert.Contains("could not confirm external cleanup", text, StringComparison.Ordinal);
        Assert.Contains("state root is denied", text, StringComparison.Ordinal);
        Assert.DoesNotContain("package-local state cleared", text, StringComparison.Ordinal);
        Assert.DoesNotContain("isolated session is ready", text, StringComparison.OrdinalIgnoreCase);
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
    public async Task ForcedFreshProvisionFailureRetainsResidualCleanupWarning()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        ((FakeMxcSessionClient)runtime.Backend).ExecuteBehavior = _ =>
        {
            WriteRuntimeInstallResult((FakeMxcSessionClient)runtime.Backend, 1);
            return Task.FromResult(new MxcExecutionResult(1, string.Empty, string.Empty));
        };
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownResult = new TeardownResult(false, "Backend cleanup remains unresolved.")
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh", "--force"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        string text = FlattenRenderedText(output.ToString());
        Assert.Equal(1, exitCode);
        Assert.Contains("could not confirm external cleanup", text, StringComparison.Ordinal);
        Assert.Contains("package-local state cleared", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForcedFreshCancellationRetainsResidualCleanupWarning()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        ((FakeMxcSessionClient)runtime.Backend).ExecuteBehavior = _ =>
            throw new OperationCanceledException();
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            TeardownResult = new TeardownResult(false, "Backend cleanup remains unresolved.")
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh", "--force"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        string text = FlattenRenderedText(output.ToString());
        Assert.Equal(1, exitCode);
        Assert.Contains("could not confirm external cleanup", text, StringComparison.Ordinal);
        Assert.Contains("cancelled", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("package-local state cleared", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryFailureReportsRuntimeInIsolatedSession()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime)
        {
            RecoveryResult = new GatewayPersistenceInstallResult(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.TaskScheduler,
                "Recovery registration failed.",
                Changed: false)
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle);

        string text = output.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("installed in the isolated session", text, StringComparison.Ordinal);
        Assert.DoesNotContain("installed in the host", text, StringComparison.Ordinal);
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
        Assert.Contains(
            "another OpenClaw process",
            secondOutput.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetupReportsMissingApplicationBeforeProbingSupport()
    {
        bool supportProbed = false;
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new ProbeCountingLifecycle(runtime, () => supportProbed = true);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.RunControlAsync(
                new HostOptions(null, null, []),
                ["setup"],
                _ => { },
                TextWriter.Null,
                TextWriter.Null,
                installationLifecycle: lifecycle));

        Assert.False(supportProbed);
    }

    [Fact]
    public async Task AgentReportsMissingApplicationBeforeProbingSupport()
    {
        bool supportProbed = false;

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.RunAgentAsync(
                new HostOptions(null, null, []),
                _ => { },
                probeReadiness: token =>
                {
                    supportProbed = true;
                    return SupportedHost(token);
                }));

        Assert.False(supportProbed);
    }

    [Fact]
    public async Task AgentFailsLoudlyOnAnUnsupportedWindowsBuild()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        bool sessionRuntimeCreated = false;

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => Program.RunAgentAsync(
                new HostOptions(applicationDirectory, null, []),
                _ => { },
                _ =>
                {
                    sessionRuntimeCreated = true;
                    return CreateSessionRuntime();
                },
                probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                    "runtime",
                    null,
                    null,
                    MxcHostSupport.Unsupported,
                    null,
                    MxcSupportEvidence.HostBuild)),
                getPackageFamilyName: () => "OpenClaw.Gateway_test",
                readEnvironmentVariable: _ => null));

        Assert.False(sessionRuntimeCreated);
        AssertRecommendsNewerWindows(failure.Message);
    }

    [Fact]
    public async Task AgentIgnoresTheRemovedSessionOptOutVariable()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        bool sessionRuntimeCreated = false;

        // A stale OPENCLAW_SESSION=0 must not reopen the removed host path.
        await Assert.ThrowsAsync<SessionException>(() => Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, []),
            _ => { },
            _ =>
            {
                sessionRuntimeCreated = true;
                return CreateSessionRuntime();
            },
            probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                "runtime",
                null,
                null,
                MxcHostSupport.Unsupported,
                null,
                MxcSupportEvidence.HostBuild)),
            getPackageFamilyName: () => "OpenClaw.Gateway_test",
            readEnvironmentVariable: name =>
                name == "OPENCLAW_SESSION" ? "0" : null));

        Assert.False(sessionRuntimeCreated);
    }

    [Fact]
    public async Task BrowserLaunchSucceedsWhenShellExecutionReturnsNoProcess()
    {
        await Program.LaunchBrowserAsync(
            "http://127.0.0.1:18789/#token=secret",
            _ => null);
    }

    [Fact]
    public async Task OpenWithoutSetupFailsNormallyWithoutStartingWork()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        var lifecycle = new FailingFreshLifecycle(runtime);
        var backend = (FakeMxcSessionClient)runtime.Backend;
        bool browserLaunched = false;
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []),
            ["open"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle,
            launchBrowserAsync: _ =>
            {
                browserLaunched = true;
                return Task.CompletedTask;
            });

        string text = FlattenRenderedText(output.ToString());
        Assert.Equal(1, exitCode);
        Assert.Contains("has not been set up", text, StringComparison.Ordinal);
        Assert.Contains("clawctl setup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("This shouldn't happen", text, StringComparison.Ordinal);
        Assert.Empty(backend.Calls);
        Assert.False(browserLaunched);
    }

    [Fact]
    public async Task OpenWithIncompleteSetupFailsNormallyWithoutStartingWork()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        runtime.SetupState.Write(new SetupRecord
        {
            ApplicationId = runtime.ApplicationId,
            Phase = SetupPhase.Preparing
        });
        var lifecycle = new FailingFreshLifecycle(runtime);
        var backend = (FakeMxcSessionClient)runtime.Backend;
        bool browserLaunched = false;
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []),
            ["open"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: lifecycle,
            launchBrowserAsync: _ =>
            {
                browserLaunched = true;
                return Task.CompletedTask;
            });

        string text = FlattenRenderedText(output.ToString());
        Assert.Equal(1, exitCode);
        Assert.Contains("setup is incomplete", text, StringComparison.Ordinal);
        Assert.Contains("clawctl setup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("This shouldn't happen", text, StringComparison.Ordinal);
        Assert.Empty(backend.Calls);
        Assert.False(browserLaunched);
    }

    [Fact]
    public async Task OpenWithNoRunningGatewayDoesNotRunTheDashboardOrLaunchTheBrowser()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        var backend = (FakeMxcSessionClient)runtime.Backend;
        int initialBackendCalls = backend.Calls.Count;
        bool browserLaunched = false;
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(
                Path.Combine(_testDirectory, "app"),
                Path.Combine(_testDirectory, "node-v24.15.0-win-x64.zip"),
                []),
            ["open"],
            _ => { },
            output,
            TextWriter.Null,
            installationLifecycle: new StubbedRecoveryLifecycle(runtime),
            launchBrowserAsync: _ =>
            {
                browserLaunched = true;
                return Task.CompletedTask;
            });

        string text = FlattenRenderedText(output.ToString());
        Assert.Equal(1, exitCode);
        Assert.Contains("clawctl gateway-service start", text, StringComparison.Ordinal);
        Assert.Equal(initialBackendCalls, backend.Calls.Count);
        Assert.False(browserLaunched);
    }

    [Theory]
    [InlineData(51789, 51789, 0, false, 0, 1, true)]
    [InlineData(3000, 18789, 0, false, 1, 0, true)]
    [InlineData(18789, 18789, 0, true, 1, 0, true)]
    [InlineData(51789, 51789, 51790, false, 0, 1, false)]
    [InlineData(51789, 0, 0, false, 1, 0, false)]
    public async Task OpenBindsBrowserActivationToTheObservedGatewayAndCurrentSession(
        int handoffPort,
        int observedPort,
        int additionalObservedPort,
        bool replaceSessionBeforeValidation,
        int expectedExitCode,
        int expectedBrowserLaunches,
        bool expectedPortOverride)
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        SessionRecord session = runtime.RequireSetup();
        var backend = (FakeMxcSessionClient)runtime.Backend;
        int[] observedPorts = observedPort == 0
            ? []
            : additionalObservedPort == 0
                ? [observedPort]
                : [observedPort, additionalObservedPort];
        string browserUrl = $"https://127.0.0.1:{handoffPort}/control#token=one-time-secret";
        var logs = new List<string>();
        var launchedUrls = new List<string>();
        SessionLaunchRequest? dashboardRequest = null;
        File.WriteAllText(
            Path.Combine(_testDirectory, "node-v24.20.0-win-x64.zip"),
            "fixture");
        string nativeRoot = Path.Combine(_testDirectory, "agent-native");
        Directory.CreateDirectory(nativeRoot);
        SetupRecord staged = runtime.SetupState.Read(runtime.ApplicationId).Record!;
        runtime.SetupState.Write(staged with { AgentNativeRoot = nativeRoot });

        runtime.GatewayState.Write(new GatewayRecord
        {
            SandboxId = session.SandboxId,
            ProcessId = 42,
            ProcessStartTimeUtc = DateTimeOffset.UtcNow,
            HelperPath = runtime.HelperPath,
            Port = 18789,
            ObservedPorts = observedPorts,
        });
        backend.ExecuteBehavior = _ =>
        {
            string workspace = backend.Metadata!.EphemeralWorkspacePath;
            string[] inspectionRequests = Directory.GetFiles(workspace, "inspect-*.json");
            if (inspectionRequests.Length == 1)
            {
                string inspectionRequestPath = inspectionRequests[0];
                SessionInspectRequest inspectionRequest = SessionInspectProtocol.ReadRequest(
                    File.ReadAllText(inspectionRequestPath));
                File.WriteAllText(
                    SessionLaunchProtocol.ResultPathFor(inspectionRequestPath),
                    SessionInspectProtocol.SerializeResult(new SessionInspectResult
                    {
                        RequestId = inspectionRequest.RequestId,
                        ProcessFound = true,
                        StartTimeMatches = true,
                        PortListening = true,
                        ListeningPorts = observedPorts,
                        ListenerOwned = true,
                    }));
                return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
            }

            string requestPath = Directory.GetFiles(workspace, "launch-*.json")
                .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            dashboardRequest = request;
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = request.RequestId,
                    Launched = true,
                    ExitCode = 0,
                }));
            return Task.FromResult(new MxcExecutionResult(
                0,
                // Mirrors the packaged upstream `dashboard --json` field layout so the
                // handoff is validated against the shape the guest actually returns.
                $$"""
                {"ok":true,"url":"https://127.0.0.1:{{handoffPort}}/","httpUrl":"https://127.0.0.1:{{handoffPort}}/","wsUrl":"ws://127.0.0.1:{{handoffPort}}","port":{{handoffPort}},"tokenIncluded":false,"browserUrl":"{{browserUrl}}","browserBootstrapExpiresAtMs":1790019149639}
                """,
                string.Empty));
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(
                Path.Combine(_testDirectory, "app"),
                Path.Combine(_testDirectory, "node-v24.20.0-win-x64.zip"),
                []),
            ["open", "--json"],
            logs.Add,
            output,
            TextWriter.Null,
            installationLifecycle: new StubbedRecoveryLifecycle(runtime),
            launchBrowserAsync: url =>
            {
                launchedUrls.Add(url);
                return Program.LaunchBrowserAsync(url, _ => null);
            },
            beforeBrowserValidation: replaceSessionBeforeValidation
                ? () => new SessionStateStore(runtime.Paths.SessionStatePath).Write(
                    session with { Generation = "replacement-generation" })
                : null);

        string rendered = output.ToString();
        Assert.Equal(expectedExitCode, exitCode);
        Assert.NotNull(dashboardRequest);
        Assert.Equal(
            [Path.Combine(_testDirectory, "app", "openclaw.mjs"), "dashboard", "--json"],
            dashboardRequest.Arguments);
        Assert.Contains(
            OpenClawRuntimeEnvironment.NativeRedirectFileName,
            dashboardRequest.NodeOptionsSuffix,
            StringComparison.Ordinal);
        Assert.Equal(nativeRoot, dashboardRequest.NativeRootPath);
        foreach ((string name, string value) in OpenClawRuntimeEnvironment.BuildNativeRedirect(
            Path.Combine(_testDirectory, "app"),
            nativeRoot))
        {
            Assert.Equal(value, dashboardRequest.Environment![name]);
        }
        if (expectedPortOverride)
        {
            Assert.Equal(
                observedPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                dashboardRequest.Environment![GatewayConfigurationStore.PortVariable]);
        }
        else
        {
            Assert.False(
                dashboardRequest.Environment!.ContainsKey(GatewayConfigurationStore.PortVariable));
        }
        Assert.Equal(expectedBrowserLaunches, launchedUrls.Count);
        if (expectedBrowserLaunches == 1)
        {
            Assert.Equal(browserUrl, launchedUrls[0]);
        }
        using JsonDocument result = JsonDocument.Parse(rendered);
        Assert.Equal(expectedExitCode == 0, result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("open", result.RootElement.GetProperty("command").GetString());
        Assert.Equal("running", result.RootElement.GetProperty("gateway").GetProperty("state").GetString());
        Assert.DoesNotContain(browserUrl, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("one-time-secret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(browserUrl, string.Join(Environment.NewLine, logs), StringComparison.Ordinal);
        Assert.DoesNotContain("one-time-secret", string.Join(Environment.NewLine, logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRunningGatewayHandlesBrowserShellFailureWithoutLeakingTheAuthenticatedUrl()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        SessionRecord session = runtime.RequireSetup();
        var backend = (FakeMxcSessionClient)runtime.Backend;
        const string browserUrl = "https://127.0.0.1:18789/control#token=one-time-secret";
        var logs = new List<string>();
        File.WriteAllText(
            Path.Combine(_testDirectory, "node-v24.20.0-win-x64.zip"),
            "fixture");

        runtime.GatewayState.Write(new GatewayRecord
        {
            SandboxId = session.SandboxId,
            ProcessId = 42,
            ProcessStartTimeUtc = DateTimeOffset.UtcNow,
            HelperPath = runtime.HelperPath,
            Port = 18789,
            ObservedPorts = [18789],
        });
        backend.ExecuteBehavior = _ =>
        {
            string workspace = backend.Metadata!.EphemeralWorkspacePath;
            string[] inspectionRequests = Directory.GetFiles(workspace, "inspect-*.json");
            if (inspectionRequests.Length == 1)
            {
                string inspectionRequestPath = inspectionRequests[0];
                SessionInspectRequest inspectionRequest = SessionInspectProtocol.ReadRequest(
                    File.ReadAllText(inspectionRequestPath));
                File.WriteAllText(
                    SessionLaunchProtocol.ResultPathFor(inspectionRequestPath),
                    SessionInspectProtocol.SerializeResult(new SessionInspectResult
                    {
                        RequestId = inspectionRequest.RequestId,
                        ProcessFound = true,
                        StartTimeMatches = true,
                        PortListening = true,
                        ListeningPorts = [18789],
                        ListenerOwned = true,
                    }));
                return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
            }

            string requestPath = Directory.GetFiles(workspace, "launch-*.json")
                .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = request.RequestId,
                    Launched = true,
                    ExitCode = 0,
                }));
            return Task.FromResult(new MxcExecutionResult(
                0,
                $$"""{"ok":true,"browserUrl":"{{browserUrl}}"}""",
                string.Empty));
        };
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(
                Path.Combine(_testDirectory, "app"),
                Path.Combine(_testDirectory, "node-v24.20.0-win-x64.zip"),
                []),
            ["open", "--json"],
            logs.Add,
            output,
            TextWriter.Null,
            installationLifecycle: new StubbedRecoveryLifecycle(runtime),
            launchBrowserAsync: url => Program.LaunchBrowserAsync(
                url,
                _ => throw new NotSupportedException($"shell activation failed for {browserUrl}")));

        string rendered = output.ToString();
        Assert.NotEqual(0, exitCode);
        Assert.Contains("default browser could not be opened", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(browserUrl, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("one-time-secret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(browserUrl, string.Join(Environment.NewLine, logs), StringComparison.Ordinal);
        Assert.DoesNotContain("one-time-secret", string.Join(Environment.NewLine, logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndeterminedSupportStillProvisionsTheSession()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        bool sessionRuntimeCreated = false;

        await Assert.ThrowsAsync<SessionException>(() => Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, []),
            _ => { },
            _ =>
            {
                sessionRuntimeCreated = true;
                throw new SessionException("the backend decided");
            },
            probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                "runtime",
                null,
                null,
                MxcHostSupport.Unknown,
                null,
                MxcSupportEvidence.None,
                null,
                "the host detector could not run")),
            getPackageFamilyName: () => "OpenClaw.Gateway_test",
            readEnvironmentVariable: _ => null));

        Assert.True(sessionRuntimeCreated);
    }

    [Fact]
    public async Task SessionFailureSurfacesInsteadOfFallingBack()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => Program.RunAgentAsync(
                new HostOptions(applicationDirectory, null, []),
                _ => { },
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

        Assert.Equal("setup failed", failure.Message);
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

        public Task EnsureSessionSupportedAsync(
            CancellationToken cancellationToken) => Task.CompletedTask;

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

        // Reported rather than queried, for the same reason the install is
        // stubbed: a test must not touch a real scheduled task.
        public Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayPersistenceStatus(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured."));
    }

    private static string SandboxIdFor(string applicationId) =>
        $"iso:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{{\"appId\":\"{applicationId}\"}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    private static void AssertRecommendsNewerWindows(string message)
    {
        Assert.Contains("newer version of Windows", message, StringComparison.Ordinal);
        Assert.Contains("Windows Update", message, StringComparison.Ordinal);
        Assert.Contains("#requirements", message, StringComparison.Ordinal);
    }

    private Task<int> RunAgentOnSupportedHostAsync(SessionRuntime runtime) =>
        Program.RunAgentAsync(
            CreateAgentOptions(),
            _ => { },
            _ => runtime,
            probeReadiness: SupportedHost,
            getPackageFamilyName: () => "OpenClaw.Gateway_test",
            readEnvironmentVariable: _ => null);

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

    private static void ConfigureConfigReadiness(
        FakeMxcSessionClient backend,
        SessionConfigReadinessState state,
        SessionConfigReadinessReason reason)
    {
        backend.ExecuteBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                backend.Metadata!.EphemeralWorkspacePath,
                "config-readiness-*.json").Single();
            SessionConfigReadinessRequest request =
                SessionConfigReadinessProtocol.ReadRequest(
                    File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionConfigReadinessProtocol.SerializeResult(
                    new SessionConfigReadinessResult
                    {
                        RequestId = request.RequestId,
                        State = state,
                        Reason = reason
                    }));
            return Task.FromResult(
                new MxcExecutionResult(0, string.Empty, string.Empty));
        };
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

        public IInstallationStateCleaner? StateCleaner { get; init; }

        public Exception? CleanerException { get; init; }

        public Exception? TeardownException { get; init; }

        public TeardownResult? TeardownResult { get; init; }

        public bool TeardownSucceeds { get; init; }

        public GatewayPersistenceInstallResult RecoveryResult { get; init; } =
            new(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "ready",
                false);

        public TaskCompletionSource ProvisionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowProvision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TeardownCount { get; private set; }

        public SessionRuntime CreateRuntime(Action<string> log) => _runtime;

        public Task EnsureSessionSupportedAsync(
            CancellationToken cancellationToken) => Task.CompletedTask;

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
            if (StateCleaner is not null)
            {
                return StateCleaner;
            }

            Cleaner.Exception = CleanerException;
            return Cleaner;
        }

        public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            Task.FromResult(RecoveryResult);

        public Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayPersistenceStatus(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "ready"));
    }

    private sealed class ProbeCountingLifecycle(SessionRuntime runtime, Action onProbe)
        : IInstallationLifecycle
    {
        public SessionRuntime CreateRuntime(Action<string> log) => runtime;

        public Task EnsureSessionSupportedAsync(CancellationToken cancellationToken)
        {
            onProbe();
            return Task.CompletedTask;
        }

        public PackageRuntimeMetadata ValidatePackageRuntime(
            HostOptions options,
            SessionRuntime sessionRuntime) =>
            throw new NotSupportedException();

        public ISessionLockHandle AcquireLifecycleLock(SessionRuntime sessionRuntime) =>
            throw new NotSupportedException();

        public Task<TeardownResult> TeardownAsync(
            HostOptions options,
            SessionRuntime sessionRuntime,
            Action<string> log,
            bool lockAlreadyHeld,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IInstallationStateCleaner CreateStateCleaner(SessionRuntime sessionRuntime) =>
            throw new NotSupportedException();

        public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
