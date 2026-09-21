using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Behavior of the shared setup owner's implicit route, which an
/// <c>openclaw</c> launch uses on a clean machine.
/// </summary>
public sealed class SetupOrchestratorTests : IDisposable
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
    public async Task AnAbsentMarkerIsProvisionedAndRecordedAsReady()
    {
        SessionRuntime runtime = CreateRuntime();
        var lifecycle = new StubLifecycle();

        SetupEnsureOutcome outcome = await EnsureAsync(runtime, lifecycle).ConfigureAwait(true);

        Assert.Equal(SetupEnsureOutcome.Provisioned, outcome);
        SetupRecord? record = runtime.SetupState.Read(runtime.ApplicationId).Record;
        Assert.NotNull(record);
        Assert.Equal(SetupPhase.Ready, record.Phase);
        Assert.Equal(1, lifecycle.RecoveryInstalls);
    }

    [Fact]
    public async Task ACompletedInstallationIsLeftAloneWithoutDoingAnyWork()
    {
        SessionRuntime runtime = CreateRuntime();
        var lifecycle = new StubLifecycle();
        Assert.Equal(SetupEnsureOutcome.Provisioned, await EnsureAsync(runtime, lifecycle).ConfigureAwait(true));
        string sandboxId = runtime.SetupState.Read(runtime.ApplicationId).Record!.SandboxId!;

        SetupEnsureOutcome outcome = await EnsureAsync(runtime, lifecycle).ConfigureAwait(true);

        Assert.Equal(SetupEnsureOutcome.Skipped, outcome);
        // Re-provisioning would replace the recorded session out from under a
        // gateway that is already running in it.
        Assert.Equal(1, lifecycle.RecoveryInstalls);
        Assert.Equal(sandboxId, runtime.SetupState.Read(runtime.ApplicationId).Record!.SandboxId);
    }

    [Fact]
    public Task AnInterruptedPreparingMarkerIsNotRepairedImplicitly() =>
        AssertMarkerIsPreservedAsync(SetupPhase.Preparing);

    [Fact]
    public Task AnInterruptedTeardownMarkerIsNotRepairedImplicitly() =>
        AssertMarkerIsPreservedAsync(SetupPhase.TearingDown);

    private async Task AssertMarkerIsPreservedAsync(SetupPhase phase)
    {
        SessionRuntime runtime = CreateRuntime();
        runtime.SetupState.Write(new SetupRecord
        {
            ApplicationId = runtime.ApplicationId,
            Phase = phase
        });
        var lifecycle = new StubLifecycle();

        SetupEnsureOutcome outcome = await EnsureAsync(runtime, lifecycle).ConfigureAwait(true);

        // Only an absent marker is provisioned automatically. Silently
        // reprovisioning here would destroy state the user may be recovering,
        // and would hide the explicit message that names the right command.
        Assert.Equal(SetupEnsureOutcome.Skipped, outcome);
        Assert.Equal(0, lifecycle.RecoveryInstalls);
        Assert.Equal(phase, runtime.SetupState.Read(runtime.ApplicationId).Record!.Phase);
    }

    [Fact]
    public async Task AnUnreadableMarkerIsNotOverwrittenImplicitly()
    {
        SessionRuntime runtime = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.Paths.SetupStatePath)!);
        await File.WriteAllTextAsync(runtime.Paths.SetupStatePath, "{ not json")
            .ConfigureAwait(true);
        var lifecycle = new StubLifecycle();

        SetupEnsureOutcome outcome = await EnsureAsync(runtime, lifecycle).ConfigureAwait(true);

        Assert.Equal(SetupEnsureOutcome.Skipped, outcome);
        Assert.Equal(0, lifecycle.RecoveryInstalls);
        Assert.Equal(
            "{ not json",
            await File.ReadAllTextAsync(runtime.Paths.SetupStatePath).ConfigureAwait(true));
    }

    [Fact]
    public async Task AMarkerFromAnotherInstallationIsNotAdopted()
    {
        SessionRuntime runtime = CreateRuntime();
        runtime.SetupState.Write(new SetupRecord
        {
            ApplicationId = "PFN:SomeoneElse_1234567890abc",
            Phase = SetupPhase.Ready
        });
        var lifecycle = new StubLifecycle();

        SetupEnsureOutcome outcome = await EnsureAsync(runtime, lifecycle).ConfigureAwait(true);

        Assert.Equal(SetupEnsureOutcome.Skipped, outcome);
        Assert.Equal(0, lifecycle.RecoveryInstalls);
    }

    [Fact]
    public async Task AFailedRecoveryInstallReportsSetupGuidanceAndLeavesNoReadyMarker()
    {
        SessionRuntime runtime = CreateRuntime();
        var lifecycle = new StubLifecycle
        {
            Recovery = new GatewayPersistenceInstallResult(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.TaskScheduler,
                "The logon task could not be registered.",
                Changed: false,
                Detail: "Access is denied.")
        };

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => EnsureAsync(runtime, lifecycle)).ConfigureAwait(true);

        Assert.Contains("clawctl setup", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", failure.Message, StringComparison.Ordinal);
        Assert.NotEqual(
            SetupPhase.Ready,
            runtime.SetupState.Read(runtime.ApplicationId).Record?.Phase);
    }

    [Fact]
    public async Task TheLifecycleLockIsReleasedBeforeReturning()
    {
        SessionRuntime runtime = CreateRuntime();

        Assert.Equal(
            SetupEnsureOutcome.Provisioned,
            await EnsureAsync(runtime, new StubLifecycle()).ConfigureAwait(true));

        // The lifecycle lock is not re-entrant: it takes ownership on a fresh
        // dedicated thread per acquire. If ensure returned still holding it,
        // the launch that follows would block against this same process until
        // the acquire timed out.
        using ISessionLockHandle? handle = runtime.LifecycleLock.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(handle);
    }

    [Fact]
    public async Task ConcurrentFirstLaunchesProvisionExactlyOnce()
    {
        using var lifecycleLock = new CoordinatedSessionLock();
        SessionRuntime runtime = CreateRuntime(lifecycleLock);
        var lifecycle = new StubLifecycle();
        (HostOptions options, string applicationDirectory) = CreateHostOptions();

        ISessionLockHandle held =
            lifecycleLock.TryAcquire(TimeSpan.Zero) ??
            throw new InvalidOperationException("The fixture lock was unexpectedly busy.");
        lifecycleLock.ObserveNextAttempts(2);
        Task<SetupEnsureOutcome>[] ensures =
        [
            Task.Run(() => EnsureAsync(
                options,
                applicationDirectory,
                runtime,
                lifecycle)),
            Task.Run(() => EnsureAsync(
                options,
                applicationDirectory,
                runtime,
                lifecycle))
        ];
        try
        {
            await lifecycleLock.WaitForObservedAttemptsAsync().ConfigureAwait(true);
            Assert.Equal(0, lifecycle.RecoveryInstalls);
        }
        finally
        {
            held.Dispose();
        }

        SetupEnsureOutcome[] outcomes = await Task.WhenAll(ensures).ConfigureAwait(true);

        Assert.Equal(1, lifecycle.RecoveryInstalls);
        Assert.Single(outcomes, SetupEnsureOutcome.Provisioned);
        Assert.Single(outcomes, SetupEnsureOutcome.Skipped);
    }

    private Task<SetupEnsureOutcome> EnsureAsync(
        SessionRuntime runtime,
        StubLifecycle lifecycle)
    {
        (HostOptions options, string applicationDirectory) = CreateHostOptions();
        return EnsureAsync(options, applicationDirectory, runtime, lifecycle);
    }

    private (HostOptions Options, string ApplicationDirectory) CreateHostOptions()
    {
        string applicationDirectory = Path.Combine(_root, "app");
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(Path.Combine(applicationDirectory, "openclaw.mjs"), "fixture");
        string archivePath = Path.Combine(_root, "node-v24.20.0-win-x64.zip");
        File.WriteAllText(archivePath, "fixture");

        return (
            new HostOptions(applicationDirectory, archivePath, []),
            applicationDirectory);
    }

    private Task<SetupEnsureOutcome> EnsureAsync(
        HostOptions options,
        string applicationDirectory,
        SessionRuntime runtime,
        StubLifecycle lifecycle)
    {
        return SetupOrchestrator.EnsureAsync(
            options,
            runtime,
            lifecycle,
            applicationDirectory,
            _ => { },
            new NoProgress(),
            CancellationToken.None);
    }

    private SessionRuntime CreateRuntime(ISessionLock? lifecycleLock = null)
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
            string requestPath = Directory
                .GetFiles(_backend.Metadata!.EphemeralWorkspacePath, "runtime-*.json")
                .Single();
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
        };

        return SessionRuntime.Create(
            HostPaths.ForRoot(
                Path.Combine(_root, "state"),
                "OpenClaw.Gateway_ensuretest"),
            () => throw new InvalidOperationException("The test backend must be supplied."),
            baseDirectory,
            _ => { },
            _backend,
            lifecycleLock);
    }

    private sealed class CoordinatedSessionLock : ISessionLock, IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private TaskCompletionSource _observedAttempts =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remainingAttempts;

        public void ObserveNextAttempts(int count)
        {
            _remainingAttempts = count;
            _observedAttempts =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitForObservedAttemptsAsync() => _observedAttempts.Task;

        public void Dispose() => _semaphore.Dispose();

        public ISessionLockHandle? TryAcquire(TimeSpan timeout)
        {
            if (Volatile.Read(ref _remainingAttempts) > 0 &&
                Interlocked.Decrement(ref _remainingAttempts) == 0)
            {
                _observedAttempts.TrySetResult();
            }

            return _semaphore.Wait(timeout)
                ? new Handle(() => _semaphore.Release())
                : null;
        }

        private sealed class Handle(Action release) : ISessionLockHandle
        {
            private Action? _release = release;

            public void Dispose() =>
                Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    private sealed class NoProgress : IProgress<ClawCtlProgress>
    {
        public void Report(ClawCtlProgress value)
        {
        }
    }

    /// <summary>
    /// Only the members the implicit route is allowed to use are implemented.
    /// The rest throw, so a change that made ensure tear down, clean, or
    /// re-probe support would fail loudly rather than silently touch state.
    /// </summary>
    private sealed class StubLifecycle : IInstallationLifecycle
    {
        public int RecoveryInstalls { get; private set; }

        public GatewayPersistenceInstallResult Recovery { get; init; } =
            new(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.",
                Changed: true);

        public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
            Action<string> log,
            CancellationToken cancellationToken)
        {
            RecoveryInstalls++;
            return Task.FromResult(Recovery);
        }

        public SessionRuntime CreateRuntime(Action<string> log) =>
            throw new NotSupportedException();

        public Task EnsureSessionSupportedAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public PackageRuntimeMetadata ValidatePackageRuntime(
            HostOptions options,
            SessionRuntime runtime) =>
            throw new NotSupportedException();

        public ISessionLockHandle AcquireLifecycleLock(SessionRuntime runtime) =>
            throw new NotSupportedException();

        public Task<TeardownResult> TeardownAsync(
            HostOptions options,
            SessionRuntime runtime,
            Action<string> log,
            bool lockAlreadyHeld,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IInstallationStateCleaner CreateStateCleaner(SessionRuntime runtime) =>
            throw new NotSupportedException();

        public Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
