using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

internal sealed record GatewayRestartHandoffDecision(bool Restart, string Detail);

internal static class SessionGatewayRestartHandoff
{
    internal static GatewayRestartHandoffDecision Consume(
        SessionLaunchRequest request,
        int processId,
        WindowsKillOnCloseJob containingJob)
    {
        SessionProcessOutput output = SessionProcessLauncher.Capture(request with
        {
            Mode = SessionLaunchMode.Attached,
            Arguments = [.. request.RestartHandoffArguments!,
                "--expected-pid", processId.ToString(CultureInfo.InvariantCulture), "--json"]
        }, TimeSpan.FromSeconds(30), containingJob);
        if (output.ExitCode != 0)
        {
            throw new SessionLaunchException(
                $"The bundled Gateway restart handoff command failed (exit {output.ExitCode}); " +
                "no replacement was started. Run `clawctl collect-logs`.");
        }
        return ReadDecision(output.StandardOutput, processId);
    }

    internal static GatewayRestartHandoffDecision ReadDecision(string json, int processId)
    {
        GatewayRestartHandoffResult? result;
        try
        {
            result = JsonSerializer.Deserialize(json,
                GatewayRestartHandoffJsonContext.Default.GatewayRestartHandoffResult);
        }
        catch (JsonException exception)
        {
            throw new SessionLaunchException(
                "The bundled Gateway restart handoff command returned invalid JSON; no replacement was started.",
                exception);
        }
        if (result is not { Ok: true, Protocol: "openclaw.gateway.restart-handoff", ProtocolVersion: 1 })
        {
            throw new SessionLaunchException(
                "The bundled Gateway restart handoff command returned an unsupported contract; no replacement was started.");
        }
        if (result is { Status: "none", Reason: "missing" })
        {
            return new(false, "no restart handoff");
        }
        if (result is { Status: "rejected", Reason: "expired" or "invalid" or "pid-mismatch" })
        {
            return new(false, $"restart handoff rejected ({result.Reason})");
        }
        if (result is not { Status: "accepted", Handoff: { } handoff } ||
            handoff.Pid != processId)
        {
            throw new SessionLaunchException(
                "The bundled Gateway restart handoff command did not identify the exited process; no replacement was started.");
        }
        return handoff is { SupervisorMode: "external", RestartKind: "full-process" }
            ? new(true, "restarting the gateway after its authorized full-process handoff")
            : new(false, "restart handoff is not an external full-process restart");
    }
}

internal sealed record GatewayRestartHandoffResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("handoff")]
    public GatewayRestartHandoff? Handoff { get; init; }
}

internal sealed record GatewayRestartHandoff
{
    [JsonPropertyName("pid")]
    public int Pid { get; init; }

    [JsonPropertyName("supervisorMode")]
    public string? SupervisorMode { get; init; }

    [JsonPropertyName("restartKind")]
    public string? RestartKind { get; init; }
}

[JsonSerializable(typeof(GatewayRestartHandoffResult))]
internal sealed partial class GatewayRestartHandoffJsonContext : JsonSerializerContext;
