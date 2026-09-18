using OpenClaw.Launcher.Mxc;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Session;

internal enum AgentConfigReadinessState
{
    Absent,
    NotReady,
    StartupEligible,
    Unavailable,
    Unknown
}

internal sealed record AgentConfigReadinessStatus(
    AgentConfigReadinessState State,
    SessionConfigReadinessReason? Reason = null,
    string? Detail = null,
    bool ProbeFailed = false);

internal static class AgentConfigReadinessProbe
{
    public static async Task<AgentConfigReadinessStatus> CheckAsync(
        SessionRuntime runtime,
        SessionStatus session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(session);

        if (session.Availability == SessionAvailability.None)
        {
            return new AgentConfigReadinessStatus(
                AgentConfigReadinessState.Unavailable,
                Detail: session.Detail);
        }

        if (session.Availability != SessionAvailability.Running ||
            session.Record is null)
        {
            return new AgentConfigReadinessStatus(
                AgentConfigReadinessState.Unknown,
                Detail: session.Detail,
                ProbeFailed: true);
        }

        try
        {
            SessionConfigReadinessResult result =
                await runtime.Executor.CheckConfigReadinessAsync(
                    session.Record,
                    runtime.RequireStagedHelper(session.Record),
                    cancellationToken).ConfigureAwait(false);
            return new AgentConfigReadinessStatus(
                result.State switch
                {
                    SessionConfigReadinessState.Absent =>
                        AgentConfigReadinessState.Absent,
                    SessionConfigReadinessState.NotReady =>
                        AgentConfigReadinessState.NotReady,
                    SessionConfigReadinessState.StartupEligible =>
                        AgentConfigReadinessState.StartupEligible,
                    _ => AgentConfigReadinessState.Unknown
                },
                result.Reason);
        }
        catch (Exception exception) when (
            exception is MxcException or SessionException or SessionLaunchException or
            IOException or UnauthorizedAccessException or
            InvalidOperationException or ObjectDisposedException)
        {
            return new AgentConfigReadinessStatus(
                AgentConfigReadinessState.Unknown,
                Detail: exception.Message,
                ProbeFailed: true);
        }
    }
}
