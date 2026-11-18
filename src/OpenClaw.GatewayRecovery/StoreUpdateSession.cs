using System.Text.Json;

namespace OpenClaw.GatewayRecovery;

/// <summary>
/// Example servicing handoff. Gateway ownership remains in clawctl: a stop is
/// conditional on the launch receipt, never a PID or a guessed package path.
/// </summary>
internal sealed class StoreUpdateSession(
    Func<string, string?> command,
    Action registerRestart,
    Action unregisterRestart)
{
    private string? _attemptId;
    private bool _queryAccepted;

    public bool RestartRegistered { get; private set; }

    public bool Start()
    {
        using JsonDocument? result = Read(command("gateway-service start --recovery --json"));
        if (result is null || !TryGateway(result, out JsonElement gateway) ||
            !gateway.TryGetProperty("state", out JsonElement state) ||
            state.ValueKind != JsonValueKind.String || state.GetString() != "running" ||
            !TryLaunch(gateway, out JsonElement launch) ||
            !launch.TryGetProperty("attemptId", out JsonElement attempt) ||
            attempt.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string? id = attempt.GetString();
        if (!Guid.TryParseExact(id, "N", out Guid parsed) ||
            parsed == Guid.Empty || parsed.ToString("N") != id)
        {
            return false;
        }
        _attemptId = id;
        return true;
    }

    public bool QueryEndSession(bool servicing)
    {
        if (servicing && _attemptId is not null)
        {
            registerRestart();
            RestartRegistered = true;
            _queryAccepted = true;
        }
        return true;
    }

    public void EndSession(bool ending, bool servicing)
    {
        if (!ending || !servicing || !_queryAccepted)
        {
            CancelRestart();
            return;
        }
        _queryAccepted = false;
        // A manually stopped or replaced launch must not be resurrected. Only
        // a positively confirmed stop of our observed attempt permits restart.
        using JsonDocument? result = Read(command(
            $"gateway-service stop --if-attempt-id {_attemptId} --json"));
        if (result is null || !TryGateway(result, out JsonElement gateway) ||
            !TryLaunch(gateway, out JsonElement launch) ||
            !launch.TryGetProperty("attemptId", out JsonElement stoppedAttempt) ||
            stoppedAttempt.ValueKind != JsonValueKind.String || stoppedAttempt.GetString() != _attemptId ||
            !launch.TryGetProperty("stopOutcome", out JsonElement outcome) ||
            outcome.ValueKind != JsonValueKind.String ||
            outcome.GetString() != "stopped")
        {
            CancelRestart();
        }
    }

    public void CancelRestart()
    {
        if (RestartRegistered)
        {
            unregisterRestart();
            RestartRegistered = false;
        }
        _queryAccepted = false;
    }

    private static bool TryGateway(JsonDocument document, out JsonElement gateway)
    {
        gateway = default;
        JsonElement root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("schemaVersion", out JsonElement schema) &&
            schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int version) && version == 1 &&
            root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("gateway", out gateway) && gateway.ValueKind == JsonValueKind.Object;
    }

    private static bool TryLaunch(JsonElement gateway, out JsonElement launch) =>
        gateway.TryGetProperty("launch", out launch) && launch.ValueKind == JsonValueKind.Object &&
        launch.TryGetProperty("version", out JsonElement schema) &&
        schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int version) && version == 1;

    private static JsonDocument? Read(string? json)
    {
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
