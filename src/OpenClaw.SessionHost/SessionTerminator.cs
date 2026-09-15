using System.Diagnostics;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The <c>--stop</c> mode: ends a gateway this installation started.
/// </summary>
/// <remarks>
/// Identity is verified before anything is terminated. Windows reuses process
/// identifiers, so acting on a recorded identifier alone would eventually kill
/// an unrelated process that merely inherited it.
/// </remarks>
internal static class SessionTerminator
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

        Write(writeFile, resultPath, Stop(request, readFile));
        return 0;
    }

    internal static SessionInspectResult Stop(
        SessionInspectRequest request,
        Func<string, string> readFile)
    {
        try
        {
            using Process process = Process.GetProcessById(request.ProcessId);
            _ = process.Handle;
            if (process.HasExited)
            {
                return new SessionInspectResult { RequestId = request.RequestId };
            }
            if (process.StartTime.ToUniversalTime() != request.ProcessStartTimeUtc)
            {
                return new SessionInspectResult { RequestId = request.RequestId, ProcessFound = true };
            }
            string? error = SessionInspector.ValidateImage(process, request);
            if (error is not null)
            {
                return new SessionInspectResult
                {
                    RequestId = request.RequestId,
                    ProcessFound = true,
                    StartTimeMatches = true,
                    Error = error
                };
            }

            // Kill the same verified handle, not a reopened PID. Its job owns
            // descendant lifetime; do not enumerate arbitrary reused child IDs.
            process.Kill();
            if (!process.WaitForExit(30_000))
            {
                return new SessionInspectResult { RequestId = request.RequestId, Error = "The gateway did not exit." };
            }
        }
        catch (ArgumentException)
        {
            // It exited between the observation and the kill, which is the
            // requested end state.
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or NotSupportedException or InvalidOperationException)
        {
            return new SessionInspectResult
            {
                RequestId = request.RequestId,
                Error = $"The gateway could not be stopped: {exception.Message}"
            };
        }

        return SessionInspector.Inspect(request, readFile);
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
        }
    }
}
