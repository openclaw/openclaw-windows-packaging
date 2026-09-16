using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher;

internal sealed record ClawCtlJsonDocument(
    bool Ok,
    int SchemaVersion,
    string Command,
    ClawCtlJsonSession? Session = null,
    ClawCtlJsonRuntime? Runtime = null,
    ClawCtlJsonGateway? Gateway = null,
    ClawCtlJsonRecovery? Recovery = null,
    ClawCtlJsonBundle? Bundle = null,
    ClawCtlJsonWarning? Warning = null,
    ClawCtlJsonError? Error = null);

internal sealed record ClawCtlJsonSession(
    string State,
    string? SandboxId = null,
    string? NodeVersion = null);

internal sealed record ClawCtlJsonRuntime(string NodeVersion);

internal sealed record ClawCtlJsonGateway(string State, int? Port = null);

internal sealed record ClawCtlJsonRecovery(string State);

internal sealed record ClawCtlJsonBundle(
    string? Path,
    string Included,
    IReadOnlyList<string> Notes);

internal sealed record ClawCtlJsonWarning(string Message, string? Detail = null);

internal sealed record ClawCtlJsonError(string Type, string Message);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClawCtlJsonDocument))]
internal sealed partial class ClawCtlJsonContext : JsonSerializerContext;

internal static class ClawCtlJson
{
    private const int SchemaVersion = 1;

    internal static void WriteResult(TextWriter output, IClawCtlResult result)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(result);

        ClawCtlJsonDocument document = result switch
        {
            SetupCommandResult setup => FromSetup(setup),
            StatusCommandResult status => FromStatus(status),
            CollectLogsCommandResult logs => FromCollectLogs(logs),
            TeardownCommandResult teardown => FromTeardown(teardown),
            GatewayCommandResult gateway => FromGateway(gateway),
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.GetType().FullName,
                "Unknown clawctl result type.")
        };

        output.WriteLine(JsonSerializer.Serialize(
            document,
            ClawCtlJsonContext.Default.ClawCtlJsonDocument));
    }

    internal static void WriteFailure(TextWriter output, string command, string message)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Write(output, new ClawCtlJsonDocument(
            false,
            SchemaVersion,
            command,
            Error: new ClawCtlJsonError("cli_error", NormalizeMessage(message))));
    }

    private static ClawCtlJsonDocument FromSetup(SetupCommandResult result)
    {
        string? error = result.Error ??
            (result.Recovery?.State == GatewayPersistenceState.Ready
                ? null
                : result.Recovery?.Detail ?? result.Recovery?.Message);
        if (error is not null)
        {
            return new ClawCtlJsonDocument(
                false,
                SchemaVersion,
                result.Command,
                Error: new ClawCtlJsonError("cli_error", NormalizeMessage(error)));
        }

        return new ClawCtlJsonDocument(
            true,
            SchemaVersion,
            result.Command,
            Session: new ClawCtlJsonSession(
                result.SessionReady ? "ready" : "not-configured",
                result.SandboxId),
            Runtime: result.NodeVersion is null
                ? null
                : new ClawCtlJsonRuntime(result.NodeVersion),
            Recovery: result.Recovery is null
                ? null
                : new ClawCtlJsonRecovery(DescribeRecovery(result.Recovery.State)),
            Warning: result.Warning is null
                ? null
                : new ClawCtlJsonWarning(
                    NormalizeMessage(result.Warning.Message),
                    NormalizeOptionalMessage(result.Warning.Detail)));
    }

    private static ClawCtlJsonDocument FromStatus(StatusCommandResult result) =>
        new(
            result.ExitCode == 0,
            SchemaVersion,
            result.Command,
            Session: new ClawCtlJsonSession(
                DescribeSession(result.Session.Availability),
                result.Session.Record?.SandboxId,
                result.NodeVersion),
            Gateway: new ClawCtlJsonGateway(
                DescribeGateway(result.Gateway.State),
                GetSinglePort(result.Gateway.Record)),
            Recovery: new ClawCtlJsonRecovery(DescribeRecovery(result.Recovery.State)),
            Error: result.ExitCode == 0
                ? null
                : new ClawCtlJsonError(
                    "cli_error",
                    "One or more status checks require attention."));

    private static ClawCtlJsonDocument FromCollectLogs(CollectLogsCommandResult result) =>
        new(
            true,
            SchemaVersion,
            result.Command,
            Bundle: new ClawCtlJsonBundle(
                result.Bundle.BundlePath,
                result.Bundle.SessionReached ? "host-and-session" : "host-only",
                result.Bundle.Notes));

    private static ClawCtlJsonDocument FromTeardown(TeardownCommandResult result) =>
        result.Teardown.Succeeded
            ? new ClawCtlJsonDocument(
                true,
                SchemaVersion,
                result.Command,
                Session: new ClawCtlJsonSession(
                    result.Teardown.SessionRemoved ? "removed" : "not-configured"),
                Gateway: new ClawCtlJsonGateway("records-removed"))
            : new ClawCtlJsonDocument(
                false,
                SchemaVersion,
                result.Command,
                Error: new ClawCtlJsonError(
                    "cli_error",
                    NormalizeMessage(result.Teardown.Detail ?? result.Teardown.Message)));

    private static ClawCtlJsonDocument FromGateway(GatewayCommandResult result) =>
        result.ExitCode == 0
            ? new ClawCtlJsonDocument(
                true,
                SchemaVersion,
                result.Command,
                Gateway: new ClawCtlJsonGateway(DescribeGateway(result.State), result.Port))
            : new ClawCtlJsonDocument(
                false,
                SchemaVersion,
                result.Command,
                Error: new ClawCtlJsonError(
                    "cli_error",
                    NormalizeMessage(result.Detail ?? result.Message)));

    private static void Write(TextWriter output, ClawCtlJsonDocument document) =>
        output.WriteLine(JsonSerializer.Serialize(
            document,
            ClawCtlJsonContext.Default.ClawCtlJsonDocument));

    private static int? GetSinglePort(GatewayRecord? record) =>
        record?.ObservedPorts is { Count: 1 } ports ? ports[0] : null;

    private static string DescribeSession(SessionAvailability state) =>
        state switch
        {
            SessionAvailability.None => "not-configured",
            SessionAvailability.Running => "running",
            SessionAvailability.Stale => "stale",
            SessionAvailability.BackendUnavailable => "unavailable",
            SessionAvailability.BackendError => "failed",
            SessionAvailability.Unusable => "unusable",
            _ => "unknown"
        };

    private static string DescribeGateway(GatewayState state) =>
        state switch
        {
            GatewayState.NotStarted => "not-started",
            GatewayState.Running => "running",
            GatewayState.Stopped => "stopped",
            GatewayState.Unhealthy => "unhealthy",
            GatewayState.Starting => "starting",
            _ => "unknown"
        };

    private static string DescribeRecovery(GatewayPersistenceState state) =>
        state switch
        {
            GatewayPersistenceState.NotInstalled => "not-configured",
            GatewayPersistenceState.Ready => "configured",
            GatewayPersistenceState.ActionRequired => "action-required",
            _ => "unknown"
        };

    private static string NormalizeMessage(string message)
    {
        string normalized = message.Trim().TrimEnd('.');
        return normalized.Length == 0
            ? normalized
            : char.ToLowerInvariant(normalized[0]) + normalized[1..];
    }

    private static string? NormalizeOptionalMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? null : NormalizeMessage(message);
}
