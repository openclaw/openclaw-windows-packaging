using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher;

internal sealed record ClawCtlJsonDocument(
    bool Ok,
    int SchemaVersion,
    string Command,
    ClawCtlJsonBuild? Package = null,
    ClawCtlJsonBuild? Payload = null,
    ClawCtlJsonSession? Session = null,
    ClawCtlJsonRuntime? Runtime = null,
    ClawCtlJsonGateway? Gateway = null,
    ClawCtlJsonRecovery? Recovery = null,
    ClawCtlJsonBundle? Bundle = null,
    ClawCtlJsonWarning? Warning = null,
    ClawCtlJsonError? Error = null);

// A version and the commit that produced it. Reported for the package and for
// the OpenClaw payload it carries.
internal sealed record ClawCtlJsonBuild(string Version, string Commit);

internal sealed record ClawCtlJsonSession(
    string State,
    string? SandboxId = null,
    string? NodeVersion = null);

internal sealed record ClawCtlJsonRuntime(string NodeVersion);

// The url is the Control UI address, which is what a caller would open or
// hand to a browser. The token command is deliberately absent: it is human
// guidance, and a script that wants the token should run that command itself
// rather than parse a suggestion out of a document.
internal sealed record ClawCtlJsonGateway(
    string State,
    int? Port = null,
    string? Url = null,
    ClawCtlJsonReadiness? Readiness = null);

internal sealed record ClawCtlJsonReadiness(
    string State,
    string? Reason = null,
    string? Detail = null);

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
            OpenCommandResult open => FromOpen(open),
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

    // The build identity is known at compile time, so the version document is
    // produced directly rather than from a command result. --version is handled
    // by the version option before command dispatch and has no result to
    // project.
    internal static void WriteVersion(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        Write(output, new ClawCtlJsonDocument(
            true,
            SchemaVersion,
            "version",
            Package: new ClawCtlJsonBuild(
                ClawCtlBuildMetadata.PackageVersion,
                ClawCtlBuildMetadata.PackageCommit),
            Payload: new ClawCtlJsonBuild(
                ClawCtlBuildMetadata.PayloadVersion,
                ClawCtlBuildMetadata.PayloadCommit)));
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
                GatewayAddress.ResolvePort(result.Gateway.Record),
                Readiness: FromReadiness(result.Readiness)),
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
                Gateway: new ClawCtlJsonGateway(
                    DescribeGateway(result.State),
                    result.Port,
                    result.Url,
                    FromReadiness(result.Readiness)))
            : new ClawCtlJsonDocument(
                false,
                SchemaVersion,
                result.Command,
                Gateway: result.Readiness is null
                    ? null
                    : new ClawCtlJsonGateway(
                        DescribeGateway(result.State),
                        result.Port,
                        result.Url,
                        FromReadiness(result.Readiness)),
                Error: new ClawCtlJsonError(
                    "cli_error",
                    NormalizeMessage(result.Detail ?? result.Message)));

    private static ClawCtlJsonDocument FromOpen(OpenCommandResult result) =>
        new(
            result.ExitCode == 0,
            SchemaVersion,
            result.Command,
            Gateway: result.State is { } state
                ? new ClawCtlJsonGateway(DescribeGateway(state))
                : null,
            Error: result.ExitCode == 0
                ? null
                : new ClawCtlJsonError("cli_error", NormalizeMessage(result.Message)));

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

    private static ClawCtlJsonReadiness? FromReadiness(
        AgentConfigReadinessStatus? readiness) =>
        readiness is null
            ? null
            : new ClawCtlJsonReadiness(
                readiness.State switch
                {
                    AgentConfigReadinessState.Absent => "absent",
                    AgentConfigReadinessState.NotReady => "not-ready",
                    AgentConfigReadinessState.StartupEligible => "startup-eligible",
                    AgentConfigReadinessState.Unavailable => "unavailable",
                    _ => "unknown"
                },
                readiness.Reason is null
                    ? null
                    : readiness.Reason.Value switch
                    {
                        SessionConfigReadinessReason.ConfigFileMissing =>
                            "config-file-missing",
                        SessionConfigReadinessReason.ConfigFileUnreadable =>
                            "config-file-unreadable",
                        SessionConfigReadinessReason.ConfigFileInvalid =>
                            "config-file-invalid",
                        SessionConfigReadinessReason.GatewayMissing =>
                            "gateway-missing",
                        SessionConfigReadinessReason.GatewayModeMissing =>
                            "gateway-mode-missing",
                        SessionConfigReadinessReason.GatewayModeNotLocal =>
                            "gateway-mode-not-local",
                        SessionConfigReadinessReason.GatewayModeLocal =>
                            "gateway-mode-local",
                        _ => "unknown"
                    },
                NormalizeOptionalMessage(readiness.Detail));

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
