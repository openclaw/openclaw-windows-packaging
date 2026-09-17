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

    // The budget has to end the wait, or a wedged launch would hold the
    // terminal open indefinitely.
    [Fact]
    public async Task StartStopsWaitingOnceTheBudgetIsSpent()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = StartingUp();

        GatewayStartResult result = await harness.StartAsync();

        Assert.Equal(GatewayState.Starting, result.State);
        Assert.Contains("not listening yet", result.Message, StringComparison.Ordinal);

        // The elapsed time is the budget, not an unbounded spin.
        Assert.True(harness.Clock.GetUtcNow() - harness.Start >= GatewayController.ListenerWaitBudget);
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
