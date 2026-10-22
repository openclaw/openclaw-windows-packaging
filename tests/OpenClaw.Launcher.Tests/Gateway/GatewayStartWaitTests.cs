using OpenClaw.Launcher.Gateway;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// A gateway process that exists but has not bound its port yet is the normal
/// case for the first moments after launch. These cover what the start command
/// does with that window.
/// </summary>
public sealed class GatewayStartWaitTests
{
    private static SessionInspectResult Listening() => new()
    {
        ProcessFound = true,
        StartTimeMatches = true,
        PortListening = true,
        ListenerOwned = true,
        ListeningPorts = [18789]
    };

    private static SessionInspectResult StartingUp() => new()
    {
        ProcessFound = true,
        StartTimeMatches = true,
        PortListening = false,
        ListenerOwned = false
    };

    private static SessionInspectResult Gone() => new()
    {
        ProcessFound = false,
        StartTimeMatches = false,
        PortListening = false,
        ListenerOwned = false
    };

    private static SessionInspectResult Unobservable() => new()
    {
        ProcessFound = true,
        StartTimeMatches = true,
        Error = "The session could not be inspected."
    };

    // A gateway that needs a moment to bind should still be reported as
    // running, rather than handing the user a status command to run themselves.
    [Fact]
    public async Task StartWaitsForTheListenerToAppear()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.InspectionSequence.Enqueue(StartingUp());
        harness.Client.InspectionSequence.Enqueue(StartingUp());
        harness.Client.Inspection = Listening();

        GatewayStartResult result = await harness.StartAsync();

        Assert.Equal(GatewayState.Running, result.State);
        Assert.Equal([18789], result.Record.ObservedPorts);
        Assert.True(harness.Clock.WaitCount >= 2);
    }

    [Fact]
    public async Task RestartStopsTheRunningGatewayBeforeStartingItsReplacement()
    {
        using var harness = new GatewayStartHarness();
        harness.RecordRunningGateway(autostartDisabled: true);
        harness.Client.Inspection = Listening();

        GatewayRestartResult result = await harness.RestartAsync();

        Assert.True(result.Stop.Stopped);
        Assert.Equal(GatewayState.Running, result.Start!.State);
        Assert.Equal(
            ["inspect:1234", "stop:1234", "start", "inspect:1234"],
            harness.Client.Calls);
        Assert.True(result.Start!.Record.AutostartDisabled);
        Assert.Equal(1, harness.LifecycleLock.TotalAcquisitions);
    }

    [Fact]
    public async Task RestartStartsWhenNoGatewayWasRunning()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = Listening();

        GatewayRestartResult result = await harness.RestartAsync();

        Assert.True(result.Stop.Succeeded);
        Assert.False(result.Stop.Stopped);
        Assert.Equal(GatewayState.Running, result.Start!.State);
        Assert.DoesNotContain(
            harness.Client.Calls,
            call => call.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestartStartsWhenTheRecordedGatewayAlreadyExited()
    {
        using var harness = new GatewayStartHarness();
        harness.RecordRunningGateway();
        harness.Client.InspectionSequence.Enqueue(Gone());
        harness.Client.Inspection = Listening();

        GatewayRestartResult result = await harness.RestartAsync();

        Assert.True(result.Stop.Succeeded);
        Assert.False(result.Stop.Stopped);
        Assert.Equal(GatewayState.Running, result.Start!.State);
        Assert.DoesNotContain(
            harness.Client.Calls,
            call => call.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestartDoesNotLaunchWhenTheGatewayCannotBeInspected()
    {
        using var harness = new GatewayStartHarness();
        harness.RecordRunningGateway();
        harness.Client.Inspection = new SessionInspectResult { Error = "no answer" };

        GatewayRestartResult result = await harness.RestartAsync();

        Assert.False(result.Stop.Succeeded);
        Assert.Null(result.Start);
        Assert.DoesNotContain("start", harness.Client.Calls);
    }

    [Fact]
    public async Task RestartDoesNotLaunchWhenTheStopCannotBeVerified()
    {
        using var harness = new GatewayStartHarness();
        harness.RecordRunningGateway();
        harness.Client.Inspection = Listening();
        harness.Client.StopResult = new SessionInspectResult
        {
            ProcessFound = true,
            StartTimeMatches = true,
            Error = "stop could not be verified"
        };

        GatewayRestartResult result = await harness.RestartAsync();

        Assert.False(result.Stop.Succeeded);
        Assert.Null(result.Start);
        Assert.DoesNotContain("start", harness.Client.Calls);
    }

    [Fact]
    public async Task RestartDoesNotLaunchOverAnUnconfirmedLaunch()
    {
        using var harness = new GatewayStartHarness();
        harness.RecordPendingGateway();

        GatewayRestartResult result = await harness.RestartAsync();

        Assert.False(result.Stop.Succeeded);
        Assert.Null(result.Start);
        Assert.DoesNotContain("start", harness.Client.Calls);
    }

    // The budget has to end the wait, or a wedged launch would hold the
    // terminal open indefinitely.
    [Fact]
    public async Task StartStopsWaitingOnceTheBudgetIsSpent()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = StartingUp();

        GatewayStartResult result = await harness.StartAsync();

        Assert.Equal(TimeSpan.FromSeconds(90), GatewayController.ListenerWaitBudget);
        Assert.Equal(GatewayState.Starting, result.State);
        Assert.Contains("not listening yet", result.Message, StringComparison.Ordinal);
        Assert.Equal(result.Record.ProcessId, harness.Store.Read().Record!.ProcessId);
        Assert.Equal(result.Record.ProcessStartTimeUtc, harness.Store.Read().Record!.ProcessStartTimeUtc);
        Assert.False(result.Record.LaunchPending);
        Assert.Null(result.Record.ObservedPorts);

        // The elapsed time is the budget, not an unbounded spin.
        Assert.True(harness.Clock.GetUtcNow() - harness.Start >= GatewayController.ListenerWaitBudget);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AListenerConflictIsNotAcknowledgedAsStillStarting(
        bool portOccupied,
        bool ownsOtherPort)
    {
        using var harness = new GatewayStartHarness(port: 18789);
        harness.Client.Inspection = StartingUp() with
        {
            PortListening = portOccupied,
            ListeningPorts = ownsOtherPort ? [18790] : []
        };

        GatewayStartResult result = await harness.StartAsync();

        Assert.Equal(GatewayState.Unhealthy, result.State);
        Assert.Equal(result.Record.ProcessId, harness.Store.Read().Record!.ProcessId);
        Assert.True(harness.Clock.GetUtcNow() - harness.Start >= GatewayController.ListenerWaitBudget);
    }

    [Fact]
    public async Task AnAcceptedPendingLaunchRemainsObservableAndStoppable()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = StartingUp();

        GatewayStartResult start = await harness.StartAsync();
        GatewayStatusReport pending = await harness.GetStatusAsync();
        harness.Client.Inspection = Listening();
        GatewayStatusReport running = await harness.GetStatusAsync();
        GatewayStopResult stopped = await harness.StopAsync();

        Assert.Equal(GatewayState.Starting, start.State);
        Assert.Equal(GatewayState.Unhealthy, pending.State);
        Assert.Equal(GatewayState.Running, running.State);
        Assert.True(stopped.Succeeded);
        Assert.True(stopped.Stopped);
        Assert.Single(harness.Client.Calls, call => call == "start");
        Assert.Equal(GatewayStateFault.Missing, harness.Store.Read().Fault);
    }

    [Fact]
    public async Task CancelingTheListenerWaitRetainsOwnershipWithoutReturningAcceptance()
    {
        using var harness = new GatewayStartHarness();
        using var cancellation = new CancellationTokenSource();
        harness.Client.Inspection = StartingUp();
        harness.Client.DuringInspection = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.StartAsync(cancellationToken: cancellation.Token));

        GatewayRecord record = harness.Store.Read().Record!;
        Assert.Equal(1234, record.ProcessId);
        Assert.False(record.LaunchPending);
        Assert.Single(harness.Client.Calls, call => call == "start");
    }

    [Fact]
    public async Task AStillExitingSupervisorDoesNotAcknowledgeAStartedGateway()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = StartingUp() with
        {
            SupervisorState = SessionSupervisorStatus.ExitedState,
            SupervisorDetail = "the application exited with code 78"
        };

        GatewayStartResult result = await harness.StartAsync();

        Assert.Equal(GatewayState.Stopped, result.State);
        Assert.Contains("exited with code 78", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Clock.WaitCount);
        Assert.Equal(result.Record.ProcessId, harness.Store.Read().Record!.ProcessId);
    }

    // Waiting only makes sense while the process is alive and observable.
    // Spending the budget on an outcome that is already decided would make a
    // failed start feel like a hang.
    [Fact]
    public async Task StartDoesNotWaitWhenTheProcessIsAlreadyGone()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = Gone();

        GatewayStartResult result = await harness.StartAsync();

        Assert.NotEqual(GatewayState.Running, result.State);
        Assert.Equal(0, harness.Clock.WaitCount);
    }

    [Fact]
    public async Task StartDoesNotWaitWhenTheInspectionItselfFailed()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = Unobservable();

        GatewayStartResult result = await harness.StartAsync();

        Assert.Equal(GatewayState.Unknown, result.State);
        Assert.Equal(0, harness.Clock.WaitCount);
    }

    // The narration is only as good as its ordering: a user should see the
    // session prepared, then the launch, then the wait, then the outcome.
    [Fact]
    public async Task StartReportsItsStagesInOrder()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.InspectionSequence.Enqueue(StartingUp());
        harness.Client.Inspection = Listening();
        List<GatewayStartStage> stages = [];

        await harness.StartAsync(new InlineProgress(p => stages.Add(p.Stage)));

        Assert.Equal(
            [
                GatewayStartStage.PreparingSession,
                GatewayStartStage.Launching,
                GatewayStartStage.WaitingForListener,
                GatewayStartStage.Listening
            ],
            stages);
    }

    [Fact]
    public async Task AnAlreadyRunningGatewayIsReportedWithoutLaunchingAnother()
    {
        using var harness = new GatewayStartHarness();
        harness.RecordRunningGateway();
        harness.Client.Inspection = Listening();
        List<GatewayStartStage> stages = [];

        GatewayStartResult result = await harness.StartAsync(
            new InlineProgress(p => stages.Add(p.Stage)));

        Assert.True(result.AlreadyRunning);
        Assert.Contains(GatewayStartStage.AlreadyRunning, stages);
        Assert.DoesNotContain(GatewayStartStage.Launching, stages);
        Assert.DoesNotContain("start", harness.Client.Calls);
    }

    private sealed class InlineProgress(Action<GatewayStartProgress> report)
        : IProgress<GatewayStartProgress>
    {
        public void Report(GatewayStartProgress value) => report(value);
    }
}
