using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionRoutingPolicyTests
{
    private const string PackageFamilyName = "OpenClaw.Gateway_abc123";

    private static MxcReadinessReport Ready(
        bool? backendAvailable = true,
        string? runtimeUnavailableReason = null,
        MxcHostSupport hostSupport = MxcHostSupport.Supported,
        string? probeFailureReason = null) =>
        new(
            runtimeUnavailableReason is null ? @"C:\Package\mxc\x64" : null,
            null,
            runtimeUnavailableReason,
            hostSupport,
            null,
            backendAvailable is null
                ? MxcSupportEvidence.HostBuild
                : MxcSupportEvidence.BackendProbe,
            backendAvailable is null
                ? null
                : new MxcBackendProbe(backendAvailable.Value, "base-container", []),
            probeFailureReason);

    private static Func<string, string?> Environment(string? value) =>
        name => name == SessionRoutingPolicy.ModeVariable ? value : null;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetModeIsAutomatic(string? value)
    {
        Assert.Equal(
            SessionMode.Automatic,
            SessionRoutingPolicy.ReadMode(Environment(value)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("off")]
    [InlineData("no")]
    public void FalsyValuesDisableSessions(string value)
    {
        Assert.Equal(
            SessionMode.Disabled,
            SessionRoutingPolicy.ReadMode(Environment(value)));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("On")]
    [InlineData("yes")]
    public void TruthyValuesRequireSessions(string value)
    {
        Assert.Equal(
            SessionMode.Required,
            SessionRoutingPolicy.ReadMode(Environment(value)));
    }

    [Fact]
    public void UnrecognizedValueIsRejectedRatherThanTreatedAsOff()
    {
        // Ignoring it would run outside the session the user asked for.
        SessionException exception = Assert.Throws<SessionException>(
            () => SessionRoutingPolicy.ReadMode(Environment("maybe")));

        Assert.Contains("maybe", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedMachineUsesTheSessionByDefault()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Automatic,
            PackageFamilyName,
            Ready());

        Assert.Equal(SessionRouting.Session, decision.Routing);
    }

    [Fact]
    public void ProbeOverridesAnUnsupportedBuildVerdict()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Automatic,
            PackageFamilyName,
            Ready(backendAvailable: true, hostSupport: MxcHostSupport.Unsupported));

        Assert.Equal(SessionRouting.Session, decision.Routing);
    }

    [Fact]
    public void ProbeSayingUnavailableRunsDirectly()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Automatic,
            PackageFamilyName,
            Ready(backendAvailable: false));

        Assert.Equal(SessionRouting.Direct, decision.Routing);
        Assert.Contains("not available", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedBuildWithoutAProbeRunsDirectly()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Automatic,
            PackageFamilyName,
            Ready(backendAvailable: null, hostSupport: MxcHostSupport.Unsupported));

        Assert.Equal(SessionRouting.Direct, decision.Routing);
    }

    [Fact]
    public void UndeterminableSupportRunsDirectlyRatherThanGuessing()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Automatic,
            PackageFamilyName,
            Ready(backendAvailable: null, hostSupport: MxcHostSupport.Unknown));

        Assert.Equal(SessionRouting.Direct, decision.Routing);
    }

    [Fact]
    public void ProbeFailureReasonIsCarriedIntoTheExplanation()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Automatic,
            PackageFamilyName,
            Ready(
                backendAvailable: null,
                hostSupport: MxcHostSupport.Unknown,
                probeFailureReason: "the executor crashed"));

        Assert.Contains("the executor crashed", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRuntimeRunsDirectly()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Automatic,
            PackageFamilyName,
            Ready(runtimeUnavailableReason: "the runtime directory is absent"));

        Assert.Equal(SessionRouting.Direct, decision.Routing);
        Assert.Contains(
            "the runtime directory is absent",
            decision.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnpackagedBuildRunsDirectly()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Automatic,
            null,
            Ready());

        Assert.Equal(SessionRouting.Direct, decision.Routing);
        Assert.Contains("installed package", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledModeRunsDirectlyEvenWhenSupported()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Disabled,
            PackageFamilyName,
            Ready());

        Assert.Equal(SessionRouting.Direct, decision.Routing);
    }

    [Fact]
    public void RequiredModeSucceedsWhenSupported()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            SessionMode.Required,
            PackageFamilyName,
            Ready());

        Assert.Equal(SessionRouting.Session, decision.Routing);
    }

    [Theory]
    [InlineData("unpackaged")]
    [InlineData("no-runtime")]
    [InlineData("backend-unavailable")]
    [InlineData("unsupported-build")]
    public void RequiredModeFailsLoudlyRatherThanFallingBack(string scenario)
    {
        (string? packageFamilyName, MxcReadinessReport readiness) = scenario switch
        {
            "unpackaged" => (null, Ready()),
            "no-runtime" => (PackageFamilyName, Ready(runtimeUnavailableReason: "absent")),
            "backend-unavailable" => (PackageFamilyName, Ready(backendAvailable: false)),
            _ => (
                PackageFamilyName,
                Ready(backendAvailable: null, hostSupport: MxcHostSupport.Unsupported)),
        };

        SessionException exception = Assert.Throws<SessionException>(
            () => SessionRoutingPolicy.Decide(
                SessionMode.Required,
                packageFamilyName,
                readiness));

        Assert.Contains(
            SessionRoutingPolicy.ModeVariable,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDecisionCarriesAReason()
    {
        SessionRoutingDecision session = SessionRoutingPolicy.Decide(
            SessionMode.Automatic,
            PackageFamilyName,
            Ready());
        SessionRoutingDecision direct = SessionRoutingPolicy.Decide(
            SessionMode.Disabled,
            PackageFamilyName,
            Ready());

        Assert.False(string.IsNullOrWhiteSpace(session.Reason));
        Assert.False(string.IsNullOrWhiteSpace(direct.Reason));
    }
}
