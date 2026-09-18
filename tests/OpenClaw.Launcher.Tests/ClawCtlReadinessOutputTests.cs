using System.Text.Json;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlReadinessOutputTests
{
    [Theory]
    [InlineData(0, "not configured")]
    [InlineData(1, "not ready")]
    [InlineData(2, "startup eligible")]
    [InlineData(3, "unavailable")]
    [InlineData(4, "unknown")]
    public void HumanGatewayStatusDescribesEveryReadinessState(
        int stateValue,
        string expected)
    {
        var readiness = new AgentConfigReadinessStatus(
            (AgentConfigReadinessState)stateValue);
        var result = new GatewayCommandResult(
            "status",
            GatewayState.NotStarted,
            "No gateway has been started.",
            null,
            0,
            Readiness: readiness);
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(output, result);

        Assert.Contains("Readiness:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "config-file-missing")]
    [InlineData(1, "config-file-unreadable")]
    [InlineData(2, "config-file-invalid")]
    [InlineData(3, "gateway-missing")]
    [InlineData(4, "gateway-mode-missing")]
    [InlineData(5, "gateway-mode-not-local")]
    [InlineData(6, "gateway-mode-local")]
    public void JsonUsesStableReadinessReasons(
        int reasonValue,
        string expected)
    {
        var result = new GatewayCommandResult(
            "status",
            GatewayState.NotStarted,
            "No gateway has been started.",
            null,
            0,
            Readiness: new AgentConfigReadinessStatus(
                AgentConfigReadinessState.NotReady,
                (SessionConfigReadinessReason)reasonValue));
        using var output = new StringWriter();

        ClawCtlJson.WriteResult(output, result);

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            expected,
            document.RootElement
                .GetProperty("gateway")
                .GetProperty("readiness")
                .GetProperty("reason")
                .GetString());
    }

    [Fact]
    public void RunningGatewayOmitsReadinessFromHumanAndJsonOutput()
    {
        var result = new GatewayCommandResult(
            "status",
            GatewayState.Running,
            "The gateway is listening.",
            null,
            0);
        using var human = new StringWriter();
        using var json = new StringWriter();

        ClawCtlConsole.WriteResult(human, result);
        ClawCtlJson.WriteResult(json, result);

        Assert.DoesNotContain("Readiness:", human.ToString(), StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json.ToString());
        Assert.False(
            document.RootElement
                .GetProperty("gateway")
                .TryGetProperty("readiness", out _));
    }

    [Fact]
    public void StartGuidanceRequiresStartupEligibleConfig()
    {
        using var eligible = new StringWriter();
        using var absent = new StringWriter();

        ClawCtlConsole.WriteResult(
            eligible,
            new GatewayCommandResult(
                "status",
                GatewayState.NotStarted,
                "No gateway has been started.",
                null,
                0,
                Readiness: new AgentConfigReadinessStatus(
                    AgentConfigReadinessState.StartupEligible)));
        ClawCtlConsole.WriteResult(
            absent,
            new GatewayCommandResult(
                "status",
                GatewayState.NotStarted,
                "No gateway has been started.",
                null,
                0,
                Readiness: new AgentConfigReadinessStatus(
                    AgentConfigReadinessState.Absent)));

        Assert.Contains(
            "clawctl gateway-service start",
            eligible.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "clawctl gateway-service start",
            absent.ToString(),
            StringComparison.Ordinal);
    }
}
