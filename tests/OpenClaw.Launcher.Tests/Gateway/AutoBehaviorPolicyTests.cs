using OpenClaw.Launcher.Gateway;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class AutoBehaviorPolicyTests
{
    public static IEnumerable<object?[]> GatewayDecisionTruthTable()
    {
        foreach (GatewayState gatewayState in Enum.GetValues<GatewayState>())
        {
            foreach (SessionConfigReadinessState readiness in
                Enum.GetValues<SessionConfigReadinessState>())
            {
                foreach (bool interactive in new[] { false, true })
                {
                    foreach (int exitCode in new[] { 0, 1 })
                    {
                        foreach (string? suppression in new string?[] { null, "off" })
                        {
                            GatewayAutoAction expected =
                                exitCode == 0 &&
                                interactive &&
                                readiness == SessionConfigReadinessState.StartupEligible &&
                                gatewayState is GatewayState.NotStarted or GatewayState.Stopped
                                    ? suppression is null
                                        ? GatewayAutoAction.Start
                                        : GatewayAutoAction.Hint
                                    : GatewayAutoAction.Silent;
                            yield return
                            [
                                (int)gatewayState,
                                (int)readiness,
                                interactive,
                                exitCode,
                                suppression,
                                (int)expected,
                            ];
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(GatewayDecisionTruthTable))]
    public void DecideGatewayActionAppliesTheFullEligibilityTruthTable(
        int gatewayStateValue,
        int readinessValue,
        bool interactive,
        int exitCode,
        string? suppression,
        int expectedValue)
    {
        GatewayState gatewayState = (GatewayState)gatewayStateValue;
        SessionConfigReadinessState readiness =
            (SessionConfigReadinessState)readinessValue;
        GatewayAutoAction result = AutoBehaviorPolicy.DecideGatewayAction(
            exitCode,
            interactive,
            readiness,
            gatewayState,
            name => name == OpenClawRuntimeEnvironment.AutoGatewayStartVariable
                ? suppression
                : null);

        Assert.Equal((GatewayAutoAction)expectedValue, result);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("No")]
    [InlineData("off")]
    [InlineData("OFF")]
    public void SuppressionSpellingsDisableBothAutomaticBehaviors(string value)
    {
        Assert.False(AutoBehaviorPolicy.IsAutomaticSetupEnabled(_ => value));
        Assert.Equal(
            GatewayAutoAction.Hint,
            DecideEligibleGatewayAction(_ => value));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnrecognizedSuppressionValuesLeaveBothAutomaticBehaviorsEnabled(
        string value)
    {
        Assert.True(AutoBehaviorPolicy.IsAutomaticSetupEnabled(_ => value));
        Assert.Equal(
            GatewayAutoAction.Start,
            DecideEligibleGatewayAction(_ => value));
    }

    [Fact]
    public void AbsentSuppressionLeavesBothAutomaticBehaviorsEnabled()
    {
        Assert.True(AutoBehaviorPolicy.IsAutomaticSetupEnabled(_ => null));
        Assert.Equal(
            GatewayAutoAction.Start,
            DecideEligibleGatewayAction(_ => null));
    }

    [Fact]
    public void AutomaticBehaviorsReadOnlyTheirOwnEnvironmentVariables()
    {
        List<string> setupQueries = [];
        Assert.True(AutoBehaviorPolicy.IsAutomaticSetupEnabled(name =>
        {
            setupQueries.Add(name);
            return name == OpenClawRuntimeEnvironment.AutoGatewayStartVariable
                ? "off"
                : null;
        }));
        Assert.Equal(
            [OpenClawRuntimeEnvironment.AutoSetupVariable],
            setupQueries);

        List<string> gatewayQueries = [];
        Assert.Equal(
            GatewayAutoAction.Start,
            DecideEligibleGatewayAction(name =>
            {
                gatewayQueries.Add(name);
                return name == OpenClawRuntimeEnvironment.AutoSetupVariable
                    ? "off"
                    : null;
            }));
        Assert.Equal(
            [OpenClawRuntimeEnvironment.AutoGatewayStartVariable],
            gatewayQueries);
    }

    [Fact]
    public void BuildExcludesHostSideAutomaticBehaviorPolicyVariables()
    {
        HashSet<string> queried = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> environment =
            OpenClawRuntimeEnvironment.Build(
                false,
                name =>
                {
                    queried.Add(name);
                    return name switch
                    {
                        OpenClawRuntimeEnvironment.AutoSetupVariable => "off",
                        OpenClawRuntimeEnvironment.AutoGatewayStartVariable => "off",
                        _ => null,
                    };
                });

        Assert.False(
            environment.ContainsKey(OpenClawRuntimeEnvironment.AutoSetupVariable));
        Assert.False(
            environment.ContainsKey(OpenClawRuntimeEnvironment.AutoGatewayStartVariable));
        Assert.DoesNotContain(
            OpenClawRuntimeEnvironment.AutoSetupVariable,
            queried,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            OpenClawRuntimeEnvironment.AutoGatewayStartVariable,
            queried,
            StringComparer.OrdinalIgnoreCase);
    }

    private static GatewayAutoAction DecideEligibleGatewayAction(
        Func<string, string?> readEnvironmentVariable) =>
        AutoBehaviorPolicy.DecideGatewayAction(
            0,
            true,
            SessionConfigReadinessState.StartupEligible,
            GatewayState.NotStarted,
            readEnvironmentVariable);
}
