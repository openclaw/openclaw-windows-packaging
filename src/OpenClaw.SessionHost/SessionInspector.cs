using System.ComponentModel;
using System.Diagnostics;
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
        if (request.LaunchPending)
        {
            // No verified PID was recorded before the interrupted launch, so
            // this reconciliation must never select or act on a process. The
            // caller must retain the launch intent and use owned teardown for
            // recovery rather than treating the absence of a PID as proof
            // that no process exists.
            return new SessionInspectResult
            {
                RequestId = request.RequestId,
                Error = "The gateway launch was not confirmed; process ownership cannot be established."
            };
        }

        try
        {
            using Process process = Process.GetProcessById(request.ProcessId);
            _ = process.Handle;
            if (process.HasExited)
            {
                return NotFound(request, readFile);
            }
            bool matches = process.StartTime.ToUniversalTime() == request.ProcessStartTimeUtc;
            string? error = matches ? ValidateImage(process, request) : null;
            SessionSupervisorStatus? supervisor = ReadSupervisorStatus(request.StatusPath, readFile);

            // Ownership is established by finding a listener belonging to this
            // process tree, not by confirming a port the host supplied. When
            // the user pinned a port, it is additionally checked against what
            // was observed, so a gateway on the wrong port is not reported as
            // healthy.
            IReadOnlyList<int> owned = matches && error is null
                ? GuestProcessObserver.ListeningPortsOwnedBy(request.ProcessId)
                : [];
            bool ownsConfigured = request.Port is not int configured ||
                owned.Contains(configured);

            return new SessionInspectResult
            {
                RequestId = request.RequestId,
                ProcessFound = true,
                StartTimeMatches = matches,
                SupervisorState = supervisor?.State,
                SupervisorDetail = supervisor?.Detail,
                PortListening = request.Port is int probe
                    ? GuestProcessObserver.AnythingListeningOn(probe)
                    : owned.Count > 0,
                ListeningPorts = owned,
                ListenerOwned = owned.Count > 0 && ownsConfigured,
                Error = error
            };
        }
        catch (ArgumentException)
        {
            return NotFound(request, readFile);
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new SessionInspectResult { RequestId = request.RequestId, Error = exception.Message };
        }
    }

    internal static string? ValidateImage(Process process, SessionInspectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.HelperPath))
        {
            return "The recorded helper image is missing; ownership cannot be verified.";
        }
        return string.Equals(process.MainModule?.FileName, request.HelperPath, StringComparison.OrdinalIgnoreCase)
            ? null : "The process image differs from the recorded gateway helper.";
    }

    private static SessionInspectResult NotFound(
        SessionInspectRequest request,
        Func<string, string> readFile)
    {
        SessionSupervisorStatus? supervisor = ReadSupervisorStatus(request.StatusPath, readFile);
        return new SessionInspectResult
        {
            RequestId = request.RequestId,
            SupervisorState = supervisor?.State,
            SupervisorDetail = supervisor?.Detail
        };
    }

    private static SessionSupervisorStatus? ReadSupervisorStatus(
        string? statusPath,
        Func<string, string> readFile)
    {
        if (string.IsNullOrWhiteSpace(statusPath))
        {
            return null;
        }

        try
        {
            return SessionInspectProtocol.ReadStatus(readFile(statusPath));
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
