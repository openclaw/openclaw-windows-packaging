using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher;

internal interface IClawCtlResult
{
    string Command { get; }

    int ExitCode { get; }
}

internal record ClawCtlProgress(string Message);

internal sealed record FreshSetupWarning(string Message, string? Detail);

internal enum SetupRuntimeLocation
{
    Host,
    IsolatedSession
}

internal sealed record SetupCommandResult(
    int ExitCode,
    string ApplicationDirectory,
    string? NodeVersion,
    GatewayPersistenceInstallResult? Recovery,
    bool SessionReady,
    FreshSetupWarning? Warning = null,
    string? Error = null,
    string? SandboxId = null,
    bool Fresh = false,
    SetupRuntimeLocation? RuntimeLocation = null,
    bool LocalStateCleared = false) : IClawCtlResult
{
    public string Command => Fresh ? "setup --fresh" : "setup";
}

internal sealed record StatusCommandResult(
    SessionStatus Session,
    GatewayStatusReport Gateway,
    GatewayPersistenceStatus Recovery,
    string? NodeVersion,
    AgentConfigReadinessStatus? Readiness = null) : IClawCtlResult
{
    public string Command => "status";

    public int ExitCode =>
        Session.Availability is SessionAvailability.Stale or
            SessionAvailability.BackendUnavailable or
            SessionAvailability.BackendError or
            SessionAvailability.Unusable ||
        Gateway.State is GatewayState.Unhealthy or GatewayState.Unknown ||
        Recovery.State is GatewayPersistenceState.ActionRequired or
            GatewayPersistenceState.Unknown ||
        Readiness?.ProbeFailed == true
            ? 1
            : 0;
}

internal sealed record CollectLogsCommandResult(
    DiagnosticsBundleResult Bundle) : IClawCtlResult
{
    public string Command => "collect-logs";

    public int ExitCode => 0;
}

internal sealed record TeardownCommandResult(
    TeardownResult Teardown) : IClawCtlResult
{
    public string Command => "teardown";

    public int ExitCode => Teardown.Succeeded ? 0 : 1;
}

internal sealed record OpenCommandResult(
    GatewayState? State,
    string Message,
    int ExitCode) : IClawCtlResult
{
    public string Command => "open";
}

internal sealed record GatewayCommandResult(
    string Action,
    GatewayState State,
    string Message,
    string? Detail,
    int ExitCode,
    int? Port = null,
    string? Url = null,
    AgentConfigReadinessStatus? Readiness = null) : IClawCtlResult
{
    public string Command => $"gateway-service {Action}";
}
