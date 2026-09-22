using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class AgentGatewayGuidanceTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private readonly List<string> _log = [];

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private AgentGatewayGuidance Create(
        SessionConfigReadinessState readinessState,
        GatewayState gatewayState,
        out GatewayGuidanceStateStore store,
        Action? startCalled = null,
        Func<
            IProgress<GatewayStartProgress>,
            Action<GatewayStartResult>,
            Task<GatewayStartResult>>? startGateway = null,
        Func<string, string?>? readEnvironmentVariable = null,
        Action? readinessCalled = null,
        Action? statusCalled = null,
        ISessionLock? lifecycleLock = null,
        Func<CancellationToken, Task<SessionConfigReadinessResult>>? checkReadiness = null,
        Func<CancellationToken, Task<GatewayStatusReport>>? getGatewayStatus = null,
        GatewayGuidanceStateStore? stateOverride = null)
    {
        store = stateOverride ?? new GatewayGuidanceStateStore(
            Path.Combine(_root, $"gateway-guidance-{Guid.NewGuid():N}.json"));
        return new AgentGatewayGuidance(
            lifecycleLock ?? new AlwaysFreeLock(),
            checkReadiness ?? (_ =>
            {
                readinessCalled?.Invoke();
                return Task.FromResult(new SessionConfigReadinessResult
                {
                    RequestId = "r1",
                    State = readinessState,
                    Reason = readinessState == SessionConfigReadinessState.StartupEligible
                        ? SessionConfigReadinessReason.GatewayModeLocal
                        : SessionConfigReadinessReason.GatewayModeMissing
                });
            }),
            getGatewayStatus ?? (_ =>
            {
                statusCalled?.Invoke();
                return Task.FromResult(new GatewayStatusReport(
                    gatewayState,
                    null,
                    gatewayState.ToString()));
            }),
            store,
            () => "logon-a",
            _log.Add,
            new FixedTimeProvider(
                new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero)),
            startGateway: startGateway ?? ((_, onRunningUnderLock) =>
            {
                startCalled?.Invoke();
                var result = new GatewayStartResult(
                    GatewayState.Running,
                    new GatewayRecord(),
                    AlreadyRunning: false,
                    "The gateway is running.");
                onRunningUnderLock(result);
                return Task.FromResult(result);
            }),
            readEnvironmentVariable: readEnvironmentVariable ?? (_ => null));
    }

    [Theory]
    [InlineData((int)GatewayState.NotStarted)]
    [InlineData((int)GatewayState.Stopped)]
    public async Task EligibleGatewayStateStartsAndAcknowledges(int stateValue)
    {
        int starts = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            (GatewayState)stateValue,
            out GatewayGuidanceStateStore store,
            () => starts++);

        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.Equal(1, starts);
        Assert.True(store.IsAcknowledged("logon-a"));
    }

    [Fact]
    public async Task EligibleGatewayStartsInsideTheLifecycleLock()
    {
        var lifecycleLock = new AlwaysFreeLock();
        bool startObservedHeldLock = false;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out _,
            startGateway: (_, onRunningUnderLock) =>
            {
                startObservedHeldLock = lifecycleLock.HeldCount == 1;
                var result = new GatewayStartResult(
                    GatewayState.Running,
                    new GatewayRecord(),
                    AlreadyRunning: false,
                    "The gateway is running.");
                onRunningUnderLock(result);
                return Task.FromResult(result);
            },
            lifecycleLock: lifecycleLock);

        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.True(startObservedHeldLock);
        Assert.Equal(1, lifecycleLock.TotalAcquisitions);
        Assert.Equal(0, lifecycleLock.HeldCount);
    }

    [Fact]
    public async Task LateAcknowledgementAfterStopSuppressesPendingAutomaticStart()
    {
        var lifecycleLock = new AlwaysFreeLock();
        var statusStarted =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStoppedStatus =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out GatewayGuidanceStateStore store,
            () => starts++,
            lifecycleLock: lifecycleLock,
            getGatewayStatus: async _ =>
            {
                statusStarted.TrySetResult();
                await allowStoppedStatus.Task.ConfigureAwait(false);
                return new GatewayStatusReport(
                    GatewayState.NotStarted,
                    null,
                    "The gateway was stopped.");
            });

        Task pending = guidance.EvaluateAsync(
            0,
            interactive: true,
            TextWriter.Null);
        await statusStarted.Task.ConfigureAwait(true);

        using (ISessionLockHandle handle =
            lifecycleLock.TryAcquire(TimeSpan.Zero) ??
            throw new InvalidOperationException("The fixture lock was unexpectedly busy."))
        {
            store.Write(
                "logon-a",
                GatewayGuidanceAcknowledgement.GatewayObservedRunning,
                DateTimeOffset.UtcNow);
        }

        allowStoppedStatus.TrySetResult();
        await pending.ConfigureAwait(true);

        Assert.Equal(0, starts);
        Assert.True(store.IsAcknowledged("logon-a"));
        Assert.Contains(
            _log,
            message => message.Contains(
                "acknowledged while postflight",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningResultWithoutUnderLockCompletionDoesNotAcknowledge()
    {
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out GatewayGuidanceStateStore store,
            startGateway: (_, _) => Task.FromResult(new GatewayStartResult(
                GatewayState.Running,
                new GatewayRecord(),
                AlreadyRunning: false,
                "The gateway is running.")));

        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.False(store.IsAcknowledged("logon-a"));
    }

    [Fact]
    public async Task AcknowledgementFailureDoesNotReportAStartFailure()
    {
        string statePath = Path.Combine(_root, "state-path-is-a-directory");
        Directory.CreateDirectory(statePath);
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out _,
            stateOverride: new GatewayGuidanceStateStore(statePath));
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            _log,
            message => message.Contains(
                "acknowledgement failed after start",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData((int)GatewayState.Starting)]
    [InlineData((int)GatewayState.Unhealthy)]
    [InlineData((int)GatewayState.Unknown)]
    public async Task UncertainGatewayNeverStartsOrAcknowledges(int stateValue)
    {
        int starts = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            (GatewayState)stateValue,
            out GatewayGuidanceStateStore store,
            () => starts++);

        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.Equal(0, starts);
        Assert.False(store.IsAcknowledged("logon-a"));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 130)]
    public async Task StartRequiresInteractiveSuccessfulOpenClaw(
        bool interactive,
        int exitCode)
    {
        int starts = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out _,
            () => starts++);

        await guidance.EvaluateAsync(exitCode, interactive, TextWriter.Null);

        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(SessionConfigReadinessState.Absent)]
    [InlineData(SessionConfigReadinessState.NotReady)]
    public async Task IneligibleConfigurationNeverStarts(
        SessionConfigReadinessState readiness)
    {
        int starts = 0;
        AgentGatewayGuidance guidance = Create(
            readiness,
            GatewayState.NotStarted,
            out _,
            () => starts++);

        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task RunningGatewayAcknowledgesWithoutStarting()
    {
        int starts = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.Running,
            out GatewayGuidanceStateStore store,
            () => starts++);

        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.Equal(0, starts);
        Assert.True(store.IsAcknowledged("logon-a"));
    }

    [Fact]
    public async Task ObservedRunningAcknowledgementSkipsLaterProbes()
    {
        int readinessCalls = 0;
        int statusCalls = 0;
        int starts = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.Running,
            out GatewayGuidanceStateStore store,
            () => starts++,
            readinessCalled: () => readinessCalls++,
            statusCalled: () => statusCalls++);

        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);
        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.True(store.IsAcknowledged("logon-a"));
        Assert.Equal(1, readinessCalls);
        Assert.Equal(1, statusCalls);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task ManualStartAcknowledgementSkipsEveryProbeAndStart()
    {
        int readinessCalls = 0;
        int statusCalls = 0;
        int starts = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out GatewayGuidanceStateStore store,
            () => starts++,
            readinessCalled: () => readinessCalls++,
            statusCalled: () => statusCalls++);

        guidance.AcknowledgeManualStart();
        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.True(store.IsAcknowledged("logon-a"));
        Assert.Equal(0, readinessCalls);
        Assert.Equal(0, statusCalls);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void BusyManualAcknowledgementDoesNotThrow()
    {
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out GatewayGuidanceStateStore store,
            lifecycleLock: new NeverFreeLock());

        guidance.AcknowledgeManualStart();

        Assert.False(store.IsAcknowledged("logon-a"));
        Assert.Contains(
            _log,
            message => message.Contains("acknowledgement failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuppressedAutomaticStartWritesTheOriginalHint()
    {
        int starts = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out _,
            () => starts++,
            readEnvironmentVariable: variable =>
                variable == OpenClawRuntimeEnvironment.AutoGatewayStartVariable ? "0" : null);
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(0, starts);
        Assert.Equal(
            AgentGatewayGuidance.Hint + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public async Task LateAcknowledgementSuppressesThePendingHint()
    {
        GatewayGuidanceStateStore? state = null;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out GatewayGuidanceStateStore created,
            readEnvironmentVariable: variable =>
                variable == OpenClawRuntimeEnvironment.AutoGatewayStartVariable ? "0" : null,
            getGatewayStatus: _ =>
            {
                state!.Write(
                    "logon-a",
                    GatewayGuidanceAcknowledgement.ManualStartInvoked,
                    DateTimeOffset.UtcNow);
                return Task.FromResult(new GatewayStatusReport(
                    GatewayState.NotStarted,
                    null,
                    "not started"));
            });
        state = created;
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            _log,
            message => message.Contains(
                "acknowledged while postflight",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task BusyFinalHintCheckSkipsTheHint()
    {
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out _,
            lifecycleLock: new NeverFreeLock(),
            readEnvironmentVariable: variable =>
                variable == OpenClawRuntimeEnvironment.AutoGatewayStartVariable ? "0" : null);
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            _log,
            message => message.Contains("lifecycle state is busy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdvisoryProbeFailureIsLoggedWithoutOutput(bool readinessFails)
    {
        var failure = new SessionLaunchException(
            readinessFails ? "readiness failed" : "status failed");
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out _,
            checkReadiness: readinessFails
                ? _ => throw failure
                : null,
            getGatewayStatus: readinessFails
                ? null
                : _ => throw failure);
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            _log,
            message => message.Contains(failure.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartFailureWritesRetryWarningAndLeavesGuidanceUnacknowledged()
    {
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out GatewayGuidanceStateStore store,
            startGateway: (_, _) => throw new SessionBusyException(TimeSpan.Zero));
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.False(store.IsAcknowledged("logon-a"));
        Assert.Contains("Gateway start failed", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "clawctl gateway-service start",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostflightPreservesTheOpenClawExitCodeWhenStartFails()
    {
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out _,
            startGateway: (_, _) => throw new SessionBusyException(TimeSpan.Zero));
        var postflight = new AgentPostflight(guidance);

        int exitCode = await postflight.RunAsync(23, interactive: true, TextWriter.Null);

        Assert.Equal(23, exitCode);
    }

    [Fact]
    public async Task SuccessfulStartAcknowledgesGuidance()
    {
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out GatewayGuidanceStateStore store);

        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.True(store.IsAcknowledged("logon-a"));
    }
}
