using System.Globalization;
using OpenClaw.GatewayRecovery;
using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Recovery;

public sealed class StoreUpdateSessionTests
{
    private const string Attempt = "1234567890abcdef1234567890abcdef";
    private const string Running = """
        {"ok":true,"schemaVersion":1,"gateway":{"state":"running","launch":{"version":1,"attemptId":"1234567890abcdef1234567890abcdef"}}}
        """;
    private const string Stopped = """
        {"ok":true,"schemaVersion":1,"gateway":{"state":"stopped","launch":{"version":1,"attemptId":"1234567890abcdef1234567890abcdef","stopOutcome":"stopped"}}}
        """;

    [Fact]
    public void AcceptsActualClawCtlStartAndConditionalStopDocuments()
    {
        static string Render(GatewayState state, GatewayLaunchReceipt receipt)
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            ClawCtlJson.WriteResult(output, new GatewayCommandResult(
                state == GatewayState.Running ? "start" : "stop", state,
                "fixture", null, 0, Launch: receipt));
            return output.ToString();
        }
        var session = new StoreUpdateSession(
            arguments => arguments.StartsWith("gateway-service start", StringComparison.Ordinal)
                ? Render(GatewayState.Running, new GatewayLaunchReceipt(Attempt, "adopted"))
                : Render(GatewayState.Stopped, new GatewayLaunchReceipt(Attempt, StopOutcome: "stopped")),
            () => { }, () => Assert.Fail("Valid receipts must preserve restart"));
        Assert.True(session.Start());
        session.QueryEndSession(servicing: true);
        session.EndSession(ending: true, servicing: true);
        Assert.True(session.RestartRegistered);
    }

    [Fact]
    public void ServicingRegistersThenStopsOnlyTheObservedAttempt()
    {
        var events = new List<string>();
        var session = Create(events, Stopped);
        Assert.True(session.Start());
        Assert.True(session.QueryEndSession(servicing: true));
        Assert.Equal(["gateway-service start --recovery --json", "register"], events);
        session.EndSession(ending: true, servicing: true);
        Assert.True(session.RestartRegistered);
        Assert.Equal($"gateway-service stop --if-attempt-id {Attempt} --json", events[^1]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CancelledShutdownOrLogoffNeverStopsGatewayAndDisarmsRestart(bool ending, bool servicing)
    {
        var events = new List<string>();
        var session = Create(events, Stopped);
        Assert.True(session.Start());
        session.QueryEndSession(servicing: true);
        session.EndSession(ending, servicing);
        Assert.False(session.RestartRegistered);
        Assert.Equal(["gateway-service start --recovery --json", "register", "unregister"], events);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"ok\":false}")]
    [InlineData("{\"ok\":true,\"schemaVersion\":1,\"gateway\":{\"launch\":null}}")]
    public void FailedOrMalformedStopDisarmsRestart(string? response)
    {
        var events = new List<string>();
        var session = Create(events, response);
        Assert.True(session.Start());
        session.QueryEndSession(servicing: true);
        session.EndSession(ending: true, servicing: true);
        Assert.False(session.RestartRegistered);
        Assert.Equal("unregister", events[^1]);
    }

    [Fact]
    public void AlreadyStoppedGatewayIsNotResurrected()
    {
        var events = new List<string>();
        var session = Create(events, Stopped.Replace("\"stopOutcome\":\"stopped\"", "\"stopOutcome\":\"not-running\"", StringComparison.Ordinal));
        Assert.True(session.Start());
        session.QueryEndSession(servicing: true);
        session.EndSession(ending: true, servicing: true);
        Assert.False(session.RestartRegistered);
    }

    [Fact]
    public void StopReceiptForAnotherAttemptDoesNotAuthorizeRestart()
    {
        var events = new List<string>();
        var session = Create(events, Stopped.Replace(Attempt, "abcdef1234567890abcdef1234567890", StringComparison.Ordinal));
        Assert.True(session.Start());
        session.QueryEndSession(servicing: true);
        session.EndSession(ending: true, servicing: true);
        Assert.False(session.RestartRegistered);
    }

    [Theory]
    [InlineData("running", "starting")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData(Attempt, "bad-argument --force")]
    public void UnsupportedStartReceiptCannotArmRestart(string oldValue, string newValue)
    {
        var session = new StoreUpdateSession(
            _ => Running.Replace(oldValue, newValue, StringComparison.Ordinal),
            () => Assert.Fail("Must not register"),
            () => Assert.Fail("Must not unregister"));
        Assert.False(session.Start());
        session.QueryEndSession(servicing: true);
        session.EndSession(ending: true, servicing: true);
        Assert.False(session.RestartRegistered);
    }

    private static StoreUpdateSession Create(List<string> events, string? stopResponse) => new(
        arguments =>
        {
            events.Add(arguments);
            return arguments.StartsWith("gateway-service start", StringComparison.Ordinal) ? Running : stopResponse;
        },
        () => events.Add("register"),
        () => events.Add("unregister"));
}
