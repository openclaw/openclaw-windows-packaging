using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests;

/// <summary>
/// What an <c>openclaw</c> launch does about setup on a machine that has never
/// been prepared.
/// </summary>
public sealed class AgentImplicitSetupTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private FakeMxcSessionClient _backend = null!;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ACleanMachineIsPreparedAndTheCommandStillRuns()
    {
        SessionRuntime runtime = CreateRuntime();
        var lifecycle = new StubLifecycle(runtime);
        var error = new StringWriter();
        string[] arguments = ["gateway", "run", "--port", "12345", "--", "a b"];

        int exitCode = await RunAgentAsync(
            runtime, lifecycle, arguments, error, childExitCode: 7)
            .ConfigureAwait(true);

        Assert.Equal(7, exitCode);
        Assert.Equal(arguments, ForwardedArguments);
        Assert.Equal(1, lifecycle.RecoveryInstalls);
        Assert.Equal(
            SetupPhase.Ready,
            runtime.SetupState.Read(runtime.ApplicationId).Record!.Phase);
        Assert.Contains(
            "Setting up OpenClaw",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task APreparedMachineLaunchesWithoutAnySetupNarration()
    {
        SessionRuntime runtime = CreateRuntime();
        var lifecycle = new StubLifecycle(runtime);
        await RunAgentAsync(runtime, lifecycle, ["status"], new StringWriter(), 0)
            .ConfigureAwait(true);

        var error = new StringWriter();
        int exitCode = await RunAgentAsync(runtime, lifecycle, ["status"], error, 0)
            .ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        // Setup already happened; an ordinary launch must stay quiet and must
        // not re-provision the session a gateway may be running in.
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(1, lifecycle.RecoveryInstalls);
    }

    [Fact]
    public async Task TheOptOutRestoresTheOriginalNotSetUpFailureAndWritesNoState()
    {
        SessionRuntime runtime = CreateRuntime();
        var lifecycle = new StubLifecycle(runtime);
        var error = new StringWriter();

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentAsync(
                runtime,
                lifecycle,
                ["status"],
                error,
                childExitCode: 0,
                readEnvironmentVariable: name =>
                    name == OpenClawRuntimeEnvironment.AutoSetupVariable ? "0" : null))
            .ConfigureAwait(true);

        Assert.Contains("clawctl setup", failure.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        // The opt-out must be inert, not merely quiet: nothing may be
        // provisioned, recorded, or registered for logon recovery.
        Assert.Null(runtime.SetupState.Read(runtime.ApplicationId).Record);
        Assert.Equal(0, lifecycle.RecoveryInstalls);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task TheOptOutIsInertOnAnAlreadyPreparedMachine()
    {
        SessionRuntime runtime = CreateRuntime();
        var lifecycle = new StubLifecycle(runtime);
        await RunAgentAsync(runtime, lifecycle, ["status"], new StringWriter(), 0)
            .ConfigureAwait(true);

        int exitCode = await RunAgentAsync(
            runtime,
            lifecycle,
            ["status"],
            new StringWriter(),
            childExitCode: 3,
            readEnvironmentVariable: name =>
                name == OpenClawRuntimeEnvironment.AutoSetupVariable ? "0" : null)
            .ConfigureAwait(true);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task AnUnsupportedHostFailsBeforeAnythingIsProvisioned()
    {
        SessionRuntime runtime = CreateRuntime();
        var lifecycle = new StubLifecycle(runtime);

        await Assert.ThrowsAsync<SessionException>(
            () => Program.RunAgentAsync(
                CreateOptions(["status"]),
                _ => { },
                _ => runtime,
                probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                    "runtime",
                    null,
                    null,
                    MxcHostSupport.Unsupported,
                    null,
                    MxcSupportEvidence.HostBuild)),
                getPackageFamilyName: () => runtime.Paths.PackageFamilyName,
                readEnvironmentVariable: _ => null,
                installationLifecycle: lifecycle)).ConfigureAwait(true);

        Assert.Null(runtime.SetupState.Read(runtime.ApplicationId).Record);
        Assert.Equal(0, lifecycle.RecoveryInstalls);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task AnInterruptedSetupIsReportedRatherThanRepairedImplicitly()
    {
        SessionRuntime runtime = CreateRuntime();
        runtime.SetupState.Write(new SetupRecord
        {
            ApplicationId = runtime.ApplicationId,
            Phase = SetupPhase.Preparing
        });
        var lifecycle = new StubLifecycle(runtime);

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentAsync(runtime, lifecycle, ["status"], new StringWriter(), 0))
            .ConfigureAwait(true);

        Assert.Contains("clawctl setup", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, lifecycle.RecoveryInstalls);
    }

    private string[]? ForwardedArguments { get; set; }

    private Task<int> RunAgentAsync(
        SessionRuntime runtime,
        StubLifecycle lifecycle,
        string[] arguments,
        TextWriter error,
        int childExitCode,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        _backend.AttachedBehavior = _ =>
        {
            string requestPath = Directory
                .GetFiles(_backend.Metadata!.EphemeralWorkspacePath, "launch-*.json")
                .Single();
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            ForwardedArguments = [.. request.Arguments!.Skip(1)];
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = request.RequestId,
                    Launched = true,
                    ExitCode = childExitCode
                }));
            return Task.FromResult(0);
        };

        return Program.RunAgentAsync(
            CreateOptions(arguments),
            _ => { },
            _ => runtime,
            probeReadiness: SupportedHost,
            getPackageFamilyName: () => runtime.Paths.PackageFamilyName,
            readEnvironmentVariable: readEnvironmentVariable ?? (_ => null),
            isInteractive: () => false,
            error: error,
            getLogonSessionId: () => "logon-implicit-setup",
            errorIsProcessConsoleWriter: () => false,
            errorIsInteractive: () => false,
            supportsUnicode: () => false,
            installationLifecycle: lifecycle);
    }

    private static Task<MxcReadinessReport> SupportedHost(CancellationToken cancellationToken) =>
        Task.FromResult(new MxcReadinessReport(
            "runtime",
            null,
            null,
            MxcHostSupport.Supported,
            null,
            MxcSupportEvidence.BackendProbe));

    private HostOptions CreateOptions(IReadOnlyList<string> arguments)
    {
        string applicationDirectory = Path.Combine(_root, "app");
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        string archivePath = Path.Combine(_root, "node-v24.20.0-win-x64.zip");
        File.WriteAllText(archivePath, "fixture");
        return new HostOptions(applicationDirectory, archivePath, arguments);
    }

    private SessionRuntime CreateRuntime()
    {
        string baseDirectory = Path.Combine(_root, "base");
        Directory.CreateDirectory(baseDirectory);
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        string helperPath = SessionRuntime.ResolveHelperPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, "fixture");

        _backend = new FakeMxcSessionClient
        {
            Metadata = new MxcProvisionMetadata(
                "agent_1",
                "S-1-5-21-0-0-0-1001",
                workspace)
        };
        _backend.ExecuteBehavior = _ =>
        {
            string workspacePath = _backend.Metadata!.EphemeralWorkspacePath;
            string[] runtimeRequests = Directory.GetFiles(workspacePath, "runtime-*.json");
            if (runtimeRequests.Length > 0)
            {
                string requestPath = runtimeRequests.Single();
                SessionRuntimeInstallRequest request = SessionRuntimeProtocol.ReadRequest(
                    File.ReadAllText(requestPath));
                File.WriteAllText(
                    SessionLaunchProtocol.ResultPathFor(requestPath),
                    SessionRuntimeProtocol.SerializeResult(new SessionRuntimeInstallResult
                    {
                        RequestId = request.RequestId,
                        ExecutablePath =
                            @"C:\Users\agent_1\AppData\Local\OpenClawGatewayMSIX\agent-node\node.exe",
                        Version = "24.20.0",
                        ArchiveName = "node-v24.20.0-win-x64.zip"
                    }));
                return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
            }

            // The postflight's file-only readiness probe. Reporting a config
            // that cannot start keeps these tests about setup rather than
            // about what the gateway does next.
            string readinessPath = Directory
                .GetFiles(workspacePath, "config-readiness-*.json").Single();
            SessionConfigReadinessRequest readinessRequest =
                SessionConfigReadinessProtocol.ReadRequest(File.ReadAllText(readinessPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(readinessPath),
                SessionConfigReadinessProtocol.SerializeResult(
                    new SessionConfigReadinessResult
                    {
                        RequestId = readinessRequest.RequestId,
                        State = SessionConfigReadinessState.Absent,
                        Reason = SessionConfigReadinessReason.ConfigFileMissing
                    }));
            return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
        };

        return SessionRuntime.Create(
            HostPaths.ForRoot(Path.Combine(_root, "state"), "OpenClaw.Gateway_implicit"),
            () => throw new InvalidOperationException("The test backend must be supplied."),
            baseDirectory,
            _ => { },
            _backend);
    }

    /// <summary>
    /// Supplies the runtime under test and a recovery install that never
    /// touches a real scheduled task. Everything the launch route must not use
    /// throws.
    /// </summary>
    private sealed class StubLifecycle(SessionRuntime runtime) : IInstallationLifecycle
    {
        public int RecoveryInstalls { get; private set; }

        public SessionRuntime CreateRuntime(Action<string> log) => runtime;

        public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
            Action<string> log,
            CancellationToken cancellationToken)
        {
            RecoveryInstalls++;
            return Task.FromResult(new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.",
                Changed: true));
        }

        public Task EnsureSessionSupportedAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
