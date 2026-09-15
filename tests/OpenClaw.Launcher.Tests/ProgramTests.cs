using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests;

public sealed class ProgramTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

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
            (nodePath, appDirectory, forwardedArguments, _, _) =>
            {
                launchAttempted = true;
                Assert.Equal(nodeRuntime.ExecutablePath, nodePath);
                Assert.Equal(applicationDirectory, appDirectory);
                Assert.Equal(arguments, forwardedArguments);
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
            "could not complete",
            output.ToString(),
            StringComparison.OrdinalIgnoreCase);
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
    public async Task FreshSetupProvisionFailureDoesNotClaimReady()
    {
        string applicationDirectory = await CreateApplicationAsync().ConfigureAwait(true);
        SessionRuntime runtime = CreateSessionRuntime();
        ((FakeMxcSessionClient)runtime.Backend).AttachedBehavior = _ =>
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
            return Task.FromResult(1);
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
        backend.AttachedBehavior = cancellationToken =>
        {
            lifecycle.ProvisionStarted.Set();
            lifecycle.AllowProvision.Wait(cancellationToken);
            WriteRuntimeInstallResult(backend, 0);
            return Task.FromResult(0);
        };
        var firstOutput = new StringWriter();
        Task<int> first = Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh"], _ => { }, firstOutput, TextWriter.Null, installationLifecycle: lifecycle);
        lifecycle.ProvisionStarted.Wait();
        using var secondOutput = new StringWriter();

        int second = await Program.RunControlAsync(
            CreateSetupOptions(applicationDirectory),
            ["setup", "--fresh"], _ => { }, secondOutput, TextWriter.Null, installationLifecycle: lifecycle);
        lifecycle.AllowProvision.Set();

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
            (_, _, _, _, _) =>
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
            readEnvironmentVariable: _ => null);

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
            (_, _, _, _, _) => Task.FromResult(0),
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
            (_, _, _, _, _) =>
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
        backend.AttachedBehavior = _ =>
        {
            WriteRuntimeInstallResult(backend, 0);
            return Task.FromResult(0);
        };
        return SessionRuntime.Create(
            HostPaths.ForRoot(stateRoot, "OpenClaw.Gateway_test"),
            () => throw new InvalidOperationException("The test backend must be supplied."),
            baseDirectory,
            _ => { },
            backend);
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

        public bool TeardownSucceeds { get; init; }

        public ManualResetEventSlim ProvisionStarted { get; } = new(false);

        public ManualResetEventSlim AllowProvision { get; } = new(false);

        public int TeardownCount { get; private set; }

        public SessionRuntime CreateRuntime(Action<string> log) => _runtime;

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

        public void Clear()
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            Cleared = true;
        }
    }

    private sealed class RecordingLockHandle : ISessionLockHandle
    {
        public void Dispose()
        {
        }
    }
}
