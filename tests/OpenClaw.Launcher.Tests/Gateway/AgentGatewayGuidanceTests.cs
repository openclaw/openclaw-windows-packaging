using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
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
        Action? readinessCalled = null,
        Action? statusCalled = null)
    {
        store = new GatewayGuidanceStateStore(
            Path.Combine(_root, "gateway-guidance.json"));
        return new AgentGatewayGuidance(
            new AlwaysFreeLock(),
            _ =>
            {
                readinessCalled?.Invoke();
                return Task.FromResult(new SessionConfigReadinessResult
                {
                    RequestId = "r1",
                    State = readinessState,
                    Reason = readinessState switch
                    {
                        SessionConfigReadinessState.Absent =>
                            SessionConfigReadinessReason.ConfigFileMissing,
                        SessionConfigReadinessState.StartupEligible =>
                            SessionConfigReadinessReason.GatewayModeLocal,
                        _ => SessionConfigReadinessReason.GatewayModeMissing
                    }
                });
            },
            _ =>
            {
                statusCalled?.Invoke();
                return Task.FromResult(new GatewayStatusReport(
                    gatewayState,
                    null,
                    gatewayState.ToString()));
            },
            store,
            () => "logon-a",
            _log.Add,
            new FixedTimeProvider(
                new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(SessionConfigReadinessState.Absent)]
    [InlineData(SessionConfigReadinessState.NotReady)]
    public async Task ConfigThatIsNotEligibleNeverChecksOrHints(
        SessionConfigReadinessState readiness)
    {
        int statusCalls = 0;
        AgentGatewayGuidance guidance = Create(
            readiness,
            GatewayState.NotStarted,
            out _,
            statusCalled: () => statusCalls++);
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(0, statusCalls);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task EligibleStoppedGatewayHintsAfterEverySuccessfulInteractiveCall(
        int stateValue)
    {
        GatewayState state = (GatewayState)stateValue;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            state,
            out _);
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);
        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(
            2,
            error.ToString().Split(AgentGatewayGuidance.Hint).Length - 1);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 130)]
    public async Task HintRequiresInteractiveSuccessfulOpenClaw(
        bool interactive,
        int exitCode)
    {
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out _);
        var error = new StringWriter();

        await guidance.EvaluateAsync(exitCode, interactive, error);

        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task UncertainGatewayNeverHintsOrAcknowledges(int stateValue)
    {
        GatewayState state = (GatewayState)stateValue;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            state,
            out GatewayGuidanceStateStore store);
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(string.Empty, error.ToString());
        Assert.False(store.IsAcknowledged("logon-a"));
    }

    [Fact]
    public async Task ObservedRunningAcknowledgesAndSkipsEveryLaterProbe()
    {
        int readinessCalls = 0;
        int statusCalls = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.Running,
            out GatewayGuidanceStateStore store,
            () => readinessCalls++,
            () => statusCalls++);

        await guidance.EvaluateAsync(1, interactive: false, TextWriter.Null);
        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.True(store.IsAcknowledged("logon-a"));
        Assert.Equal(1, readinessCalls);
        Assert.Equal(1, statusCalls);
    }

    [Fact]
    public async Task ManualStartAcknowledgementSkipsEveryProbe()
    {
        int readinessCalls = 0;
        int statusCalls = 0;
        AgentGatewayGuidance guidance = Create(
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            out GatewayGuidanceStateStore store,
            () => readinessCalls++,
            () => statusCalls++);

        guidance.AcknowledgeManualStart();
        await guidance.EvaluateAsync(0, interactive: true, TextWriter.Null);

        Assert.True(store.IsAcknowledged("logon-a"));
        Assert.Equal(0, readinessCalls);
        Assert.Equal(0, statusCalls);
    }

    [Fact]
    public void BusyManualAcknowledgementDoesNotAbortGatewayStart()
    {
        var store = new GatewayGuidanceStateStore(
            Path.Combine(_root, "gateway-guidance.json"));
        var guidance = new AgentGatewayGuidance(
            new NeverFreeLock(),
            _ => throw new InvalidOperationException("not used"),
            _ => throw new InvalidOperationException("not used"),
            store,
            () => "logon-a",
            _log.Add);

        guidance.AcknowledgeManualStart();

        Assert.False(store.IsAcknowledged("logon-a"));
        Assert.Contains(
            _log,
            message => message.Contains(
                "acknowledgement failed",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcknowledgementDuringPostflightSuppressesThePendingHint()
    {
        var store = new GatewayGuidanceStateStore(
            Path.Combine(_root, "gateway-guidance.json"));
        var readinessStarted =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReadiness =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guidance = new AgentGatewayGuidance(
            new AlwaysFreeLock(),
            async _ =>
            {
                readinessStarted.SetResult();
                await releaseReadiness.Task.ConfigureAwait(false);
                return new SessionConfigReadinessResult
                {
                    RequestId = "r1",
                    State = SessionConfigReadinessState.StartupEligible,
                    Reason = SessionConfigReadinessReason.GatewayModeLocal
                };
            },
            _ => Task.FromResult(new GatewayStatusReport(
                GatewayState.NotStarted,
                null,
                "not started")),
            store,
            () => "logon-a",
            _log.Add);
        var error = new StringWriter();

        Task evaluation = guidance.EvaluateAsync(0, interactive: true, error);
        await readinessStarted.Task.ConfigureAwait(true);
        store.Write(
            "logon-a",
            GatewayGuidanceAcknowledgement.ManualStartInvoked,
            DateTimeOffset.UtcNow);
        releaseReadiness.SetResult();
        await evaluation.ConfigureAwait(true);

        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            _log,
            message => message.Contains(
                "acknowledged while postflight",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task BusyFinalAcknowledgementCheckSkipsTheAdvisoryHint()
    {
        var store = new GatewayGuidanceStateStore(
            Path.Combine(_root, "gateway-guidance.json"));
        var guidance = new AgentGatewayGuidance(
            new NeverFreeLock(),
            _ => Task.FromResult(new SessionConfigReadinessResult
            {
                RequestId = "r1",
                State = SessionConfigReadinessState.StartupEligible,
                Reason = SessionConfigReadinessReason.GatewayModeLocal
            }),
            _ => Task.FromResult(new GatewayStatusReport(
                GatewayState.NotStarted,
                null,
                "not started")),
            store,
            () => "logon-a",
            _log.Add);
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            _log,
            message => message.Contains(
                "lifecycle state is busy",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvisoryFailureIsLoggedWithoutOutput(bool backendFailure)
    {
        var store = new GatewayGuidanceStateStore(
            Path.Combine(_root, "gateway-guidance.json"));
        Exception failure = backendFailure
            ? new MxcException(MxcErrorCode.BackendError, "backend rejected dispatch")
            : new SessionLaunchException("bad helper result");
        var guidance = new AgentGatewayGuidance(
            new AlwaysFreeLock(),
            _ => throw failure,
            _ => throw new InvalidOperationException("must not inspect"),
            store,
            () => "logon-a",
            _log.Add);
        var error = new StringWriter();

        await guidance.EvaluateAsync(0, interactive: true, error);

        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            _log,
            line => line.Contains(failure.Message, StringComparison.Ordinal));
    }
}
