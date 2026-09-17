namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// A milestone on the way from "start requested" to "the gateway is serving".
/// </summary>
/// <remarks>
/// The controller reports stages; it never writes them. Starting the gateway
/// is a lifecycle operation that also runs from a logon task, where nothing is
/// watching and any console write would be wrong, so presentation stays with
/// the caller.
/// </remarks>
internal enum GatewayStartStage
{
    /// <summary>The owned session is being started or confirmed.</summary>
    PreparingSession,

    /// <summary>A gateway matching the record is already serving.</summary>
    AlreadyRunning,

    /// <summary>The gateway process is being launched inside the session.</summary>
    Launching,

    /// <summary>The process exists; its listener has not appeared yet.</summary>
    WaitingForListener,

    /// <summary>A listener owned by the gateway is accepting connections.</summary>
    Listening,

    /// <summary>The listener did not appear within the budget.</summary>
    GaveUpWaiting
}

internal sealed record GatewayStartProgress(GatewayStartStage Stage, string Message)
    : ClawCtlProgress(Message)
{
    internal static GatewayStartProgress Initial { get; } = new(
        GatewayStartStage.PreparingSession,
        "Preparing the isolated session.");
}

/// <summary>
/// Where the gateway can be reached.
/// </summary>
/// <remarks>
/// One port carries two addresses: OpenClaw serves the Control UI over HTTP and
/// the gateway protocol over a WebSocket on the same listener. Clients such as
/// `openclaw attach` want the WebSocket form; a person wants the Control UI.
/// </remarks>
internal static class GatewayAddress
{
    /// <summary>
    /// The port the gateway is actually reachable on, or null when that is not
    /// yet known.
    /// </summary>
    /// <remarks>
    /// An observed listener wins over a configured value, because OpenClaw
    /// resolves the port from its own configuration and only the observation is
    /// true. <see cref="GatewayLaunchConfiguration.UpstreamDefaultPort"/> is
    /// deliberately not a fallback: presenting a guess as an address sends a
    /// user to a port nothing is listening on.
    /// </remarks>
    internal static int? ResolvePort(GatewayRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        if (record.ObservedPorts is not { Count: > 0 } observed)
        {
            return record.Port;
        }

        if (record.Port is int configuredPort && observed.Contains(configuredPort))
        {
            return configuredPort;
        }

        return observed.Count == 1 ? observed[0] : null;
    }
}
