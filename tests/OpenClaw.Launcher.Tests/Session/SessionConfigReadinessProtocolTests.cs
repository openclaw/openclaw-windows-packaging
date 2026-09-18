using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionConfigReadinessProtocolTests
{
    private static SessionConfigReadinessRequest ValidRequest() => new()
    {
        RequestId = "readiness-1"
    };

    private static SessionConfigReadinessResult ValidResult() => new()
    {
        RequestId = "readiness-1",
        State = SessionConfigReadinessState.StartupEligible,
        Reason = SessionConfigReadinessReason.GatewayModeLocal
    };

    [Fact]
    public void AValidRequestAndResultRoundTrip()
    {
        SessionConfigReadinessRequest request =
            SessionConfigReadinessProtocol.ReadRequest(
                SessionConfigReadinessProtocol.SerializeRequest(ValidRequest()));
        SessionConfigReadinessResult result =
            SessionConfigReadinessProtocol.ReadResult(
                SessionConfigReadinessProtocol.SerializeResult(ValidResult()),
                request.RequestId);

        Assert.Equal("readiness-1", result.RequestId);
        Assert.Equal(SessionConfigReadinessState.StartupEligible, result.State);
        Assert.Equal(SessionConfigReadinessReason.GatewayModeLocal, result.Reason);
    }

    [Fact]
    public void ARequestWithoutAnIdIsRejected()
    {
        Assert.Throws<SessionLaunchException>(
            () => SessionConfigReadinessProtocol.ReadRequest(
                SessionConfigReadinessProtocol.SerializeRequest(
                    ValidRequest() with { RequestId = null })));
    }

    [Fact]
    public void AStaleSchemaVersionIsRejected()
    {
        Assert.Throws<SessionLaunchException>(
            () => SessionConfigReadinessProtocol.ReadRequest(
                SessionConfigReadinessProtocol.SerializeRequest(
                    ValidRequest() with
                    {
                        SchemaVersion = SessionLaunchProtocol.CurrentSchemaVersion + 1
                    })));
    }

    [Fact]
    public void MalformedJsonIsRejected()
    {
        Assert.Throws<SessionLaunchException>(
            () => SessionConfigReadinessProtocol.ReadRequest("{ not-json"));
    }

    [Fact]
    public void AResultWithoutAnIdIsRejected()
    {
        Assert.Throws<SessionLaunchException>(
            () => SessionConfigReadinessProtocol.ReadResult(
                SessionConfigReadinessProtocol.SerializeResult(
                    ValidResult() with { RequestId = null })));
    }

    [Fact]
    public void AResultForAnotherRequestIsRejected()
    {
        Assert.Throws<SessionLaunchException>(
            () => SessionConfigReadinessProtocol.ReadResult(
                SessionConfigReadinessProtocol.SerializeResult(ValidResult()),
                "another-request"));
    }

    [Fact]
    public void AnInvalidEnumValueIsRejected()
    {
        SessionConfigReadinessResult result = ValidResult() with
        {
            State = (SessionConfigReadinessState)42
        };

        Assert.Throws<SessionLaunchException>(
            () => SessionConfigReadinessProtocol.ReadResult(
                SessionConfigReadinessProtocol.SerializeResult(result)));
    }

    [Fact]
    public void AnIncompatibleStateAndReasonAreRejected()
    {
        SessionConfigReadinessResult result = ValidResult() with
        {
            State = SessionConfigReadinessState.Absent
        };

        Assert.Throws<SessionLaunchException>(
            () => SessionConfigReadinessProtocol.ReadResult(
                SessionConfigReadinessProtocol.SerializeResult(result)));
    }

    [Fact]
    public void AFailureCannotAlsoClaimAClassification()
    {
        SessionConfigReadinessResult result = ValidResult() with
        {
            Error = "profile lookup failed"
        };

        Assert.Throws<SessionLaunchException>(
            () => SessionConfigReadinessProtocol.ReadResult(
                SessionConfigReadinessProtocol.SerializeResult(result)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APartialClassificationIsRejected(bool retainState)
    {
        SessionConfigReadinessResult result = ValidResult() with
        {
            State = retainState ? ValidResult().State : null,
            Reason = retainState ? null : ValidResult().Reason
        };

        Assert.Throws<SessionLaunchException>(
            () => SessionConfigReadinessProtocol.ReadResult(
                SessionConfigReadinessProtocol.SerializeResult(result)));
    }
}
