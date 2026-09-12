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
        SessionInspectResult observed = SessionInspector.Inspect(request, readFile);

        if (!observed.ProcessFound || !observed.StartTimeMatches)
        {
            // Already gone, or the identifier now belongs to someone else.
            // Either way there is nothing of ours to stop.
            return observed;
        }

        try
        {
            using Process process = Process.GetProcessById(request.ProcessId);

            // The whole tree: the gateway is a child of this process, and the
            // job that ties them together only fires when this process exits.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(30_000);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            // It exited between the observation and the kill, which is the
            // requested end state.
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return observed with
            {
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
