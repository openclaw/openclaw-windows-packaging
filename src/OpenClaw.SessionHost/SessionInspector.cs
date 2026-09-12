using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The <c>--inspect</c> mode: re-establishes whether a recorded gateway is
/// still ours and still serving.
/// </summary>
/// <remarks>
/// Every answer is an observation made now, inside the session. Nothing is
/// inferred from the recorded state itself, because a record survives the
/// process it describes: stopping the sandbox terminates detached work without
/// notifying anything.
/// </remarks>
internal static class SessionInspector
{
    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        SessionInspectRequest request;

        try
        {
            request = SessionInspectProtocol.ReadRequest(readFile(requestPath));
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            Write(
                writeFile,
                resultPath,
                new SessionInspectResult { Error = exception.Message });
            return SessionLaunchProtocol.HelperFailureExitCode;
        }

        Write(writeFile, resultPath, Inspect(request, readFile));
        return 0;
    }

    internal static SessionInspectResult Inspect(
        SessionInspectRequest request,
        Func<string, string> readFile)
    {
        DateTimeOffset? startTime =
            GuestProcessObserver.GetStartTimeUtc(request.ProcessId);

        // Creation times come from the OS at a finer resolution than the
        // recorded value round-trips through JSON, so they are compared with a
        // tolerance rather than for exact equality. The tolerance is far
        // smaller than the interval in which an identifier could plausibly be
        // reused, so it cannot let an unrelated process pass.
        bool startTimeMatches =
            startTime is { } observed &&
            (observed - request.ProcessStartTimeUtc).Duration() <
                TimeSpan.FromSeconds(2);

        return new SessionInspectResult
        {
            RequestId = request.RequestId,
            ProcessFound = startTime is not null,
            StartTimeMatches = startTimeMatches,
            SupervisorState = ReadSupervisorState(request.StatusPath, readFile),
            PortListening = GuestProcessObserver.AnythingListeningOn(request.Port),

            // Only asked of a process we have already confirmed is ours.
            // Otherwise a reused identifier could be credited with an unrelated
            // program's listener.
            ListenerOwned = startTimeMatches &&
                GuestProcessObserver.OwnsListenerOn(
                    request.Port,
                    request.ProcessId)
        };
    }

    private static string? ReadSupervisorState(
        string? statusPath,
        Func<string, string> readFile)
    {
        if (string.IsNullOrWhiteSpace(statusPath))
        {
            return null;
        }

        try
        {
            return SessionInspectProtocol.ReadStatus(readFile(statusPath)).State;
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            // A missing or unreadable status file is reported as no state,
            // never as a failed inspection: the process evidence stands on its
            // own.
            return null;
        }
    }

    private static void Write(
        Action<string, string> writeFile,
        string resultPath,
        SessionInspectResult result)
    {
        try
        {
            writeFile(resultPath, SessionInspectProtocol.SerializeResult(result));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The host reports a missing result as an unanswered inspection.
        }
    }
}
