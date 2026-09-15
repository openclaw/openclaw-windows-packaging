using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Gateway;

/// <summary>What to start inside the session.</summary>
/// <remarks>
/// <see cref="Port"/> is null unless the user pinned one, in which case
/// OpenClaw resolves the port from its own configuration.
/// </remarks>
internal sealed record GatewayStartRequest(
    string HelperPath,
    string NodePath,
    string ApplicationDirectory,
    string WorkingDirectory,
    int? Port);

/// <summary>The identity of a gateway that was started.</summary>
internal sealed record GatewayStartOutcome(
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    string StatusPath,
    string LogPath)
{
    public string? HelperPath { get; init; }
}

/// <summary>
/// Starts, inspects, and stops the gateway inside the owned session.
/// </summary>
internal interface ISessionGatewayClient
{
    Task<GatewayStartOutcome> StartAsync(
        SessionRecord session,
        GatewayStartRequest request,
        CancellationToken cancellationToken);

    Task<SessionInspectResult> InspectAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        CancellationToken cancellationToken);

    Task<SessionInspectResult> StopAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        CancellationToken cancellationToken);
}
