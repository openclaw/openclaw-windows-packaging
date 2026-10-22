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
    public void GatewayStatusCarriesStaleSessionAndMissingConfigForCompanionRecovery()
    {
        var result = new GatewayCommandResult(
            "status",
            GatewayState.Unknown,
            "The isolated session is stale.",
            null,
            1,
            Readiness: new AgentConfigReadinessStatus(
                AgentConfigReadinessState.Absent,
                SessionConfigReadinessReason.ConfigFileMissing),
            SessionAvailability: SessionAvailability.Stale);
        using var output = new StringWriter();

        ClawCtlJson.WriteResult(output, result);

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("stale", root.GetProperty("session").GetProperty("state").GetString());
        Assert.Equal("config-file-missing", root.GetProperty("gateway")
            .GetProperty("readiness").GetProperty("reason").GetString());
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

    [Fact]
    public void RunningGatewayJsonCarriesSessionAndProcessOwnershipForCompanion()
    {
        using var output = new StringWriter();
        var listener = new SessionOwnedListener
        {
            Port = 19001,
            ProcessId = 1234,
            ProcessStartTimeUtc = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
            SequenceNumber = 77
        };
        ClawCtlJson.WriteResult(output, new GatewayCommandResult(
            "status", GatewayState.Running, "Gateway is running.", null, 0,
            Port: 19001, SandboxId: "iso:test", AgentUserSid: "S-1-5-21-fixture",
            OwnedListeners: [listener]));

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("isolated-session", root.GetProperty("integration").GetProperty("kind").GetString());
        Assert.Equal(1, root.GetProperty("integration").GetProperty("version").GetInt32());
        JsonElement ownership = root.GetProperty("gateway").GetProperty("ownership");
        Assert.Equal("iso:test", ownership.GetProperty("sandboxId").GetString());
        Assert.Equal("S-1-5-21-fixture", ownership.GetProperty("agentUserSid").GetString());
        Assert.Equal(1234, ownership.GetProperty("listeners")[0].GetProperty("processId").GetInt32());
        Assert.Equal((ulong)77, ownership.GetProperty("listeners")[0].GetProperty("sequenceNumber").GetUInt64());
    }

    [Fact]
    public void RunningPortWithoutListenerIdentityIsNotOwnershipProof()
    {
        using var output = new StringWriter();
        ClawCtlJson.WriteResult(output, new GatewayCommandResult(
            "status", GatewayState.Running, "Gateway is running.", null, 0,
            Port: 19001, SandboxId: "iso:test", AgentUserSid: "S-1-5-21-fixture"));

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("gateway").TryGetProperty("ownership", out _));
    }

    [Fact]
    public void GatewayWithoutSequenceOwnershipProofOmitsOwnershipFromJson()
    {
        using var output = new StringWriter();
        ClawCtlJson.WriteResult(output, new GatewayCommandResult(
            "status", GatewayState.Running, "Gateway is running.", null, 0,
            Port: 19001, SandboxId: "iso:test", AgentUserSid: "S-1-5-21-fixture",
            OwnedListeners:
            [
                new SessionOwnedListener
                {
                    Port = 19001,
                    ProcessId = 1234,
                    ProcessStartTimeUtc = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)
                }
            ]));

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("gateway").TryGetProperty("ownership", out _));
    }

    [Fact]
    public void CompanionResponsesReturnAgentDataOnlyInStructuredOutput()
    {
        var prepared = new CompanionPrepareResult(0, Port: 19001, Token: "private-fixture-token");
        using var human = new StringWriter();
        using var json = new StringWriter();
        ClawCtlConsole.WriteResult(human, prepared);
        ClawCtlJson.WriteResult(json, prepared);

        Assert.DoesNotContain("private-fixture-token", human.ToString(), StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json.ToString());
        Assert.Equal("private-fixture-token",
            document.RootElement.GetProperty("companion").GetProperty("token").GetString());

    }
}
