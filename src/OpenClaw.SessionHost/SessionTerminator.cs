using System.ComponentModel;
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

        Write(writeFile, resultPath, Stop(request, readFile, grace: GatewayStopGrace.ConsoleInterrupt));
        return 0;
    }

    /// <remarks>
    /// A null <paramref name="grace"/> terminates at once; only the <c>--stop</c> helper, a dedicated
    /// short-lived process, can safely send the gateway its console stop request.
    /// </remarks>
    internal static SessionInspectResult Stop(
        SessionInspectRequest request,
        Func<string, string> readFile,
        Action<Process>? probeAccess = null,
        Func<IReadOnlyDictionary<int, DateTimeOffset>>? captureCreationTimes = null,
        GatewayStopGrace? grace = null)
    {
        try
        {
            using Process process = Process.GetProcessById(request.ProcessId);
            if (probeAccess is null)
            {
                _ = process.Handle;
            }
            else
            {
                probeAccess(process);
            }
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
            // Opened before the kill: the supervisor's death does not stop the gateway tree synchronously, and the
            // state lease the supervisor holds ends with it. Holding the job lets this stop wait out the whole tree.
            using WindowsKillOnCloseJob? tree = WindowsKillOnCloseJob.TryOpen(
                WindowsKillOnCloseJob.GatewayJobName(process.Id, process.StartTime.ToUniversalTime().Ticks));
            if (tree is not null)
            {
                grace?.Request(tree);
            }
            process.Kill();
            if (!process.WaitForExit(30_000))
            {
                return new SessionInspectResult { RequestId = request.RequestId, Error = "The gateway did not exit." };
            }
            if (tree is not null)
            {
                try
                {
                    tree.TerminateAndWait(TimeSpan.FromSeconds(30));
                }
                catch (TimeoutException)
                {
                    return new SessionInspectResult { RequestId = request.RequestId, Error = "The gateway process tree did not exit." };
                }
            }
        }
        catch (ArgumentException)
        {
            // It exited between the observation and the kill, which is the
            // requested end state.
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            return SessionInspector.ReconcileAccessDenied(request, exception, captureCreationTimes);
        }
        catch (Exception exception) when (
            exception is Win32Exception or NotSupportedException or InvalidOperationException)
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

/// <summary>
/// A bounded chance for a gateway to stop itself before its process tree is terminated.
/// </summary>
/// <remarks>
/// Terminating the gateway outright skips its shutdown, so it never releases its agent database
/// lease, and the next start must recover that lease as a crash. That recovery can push startup
/// past the listener wait. Termination remains the fallback: a gateway that ignores the request,
/// or is still draining work when the period ends, is terminated exactly as before.
/// </remarks>
internal sealed record GatewayStopGrace(Func<IReadOnlyList<int>, bool> Interrupt, TimeSpan Period)
{
    /// <summary>The <c>--stop</c> helper's request: Ctrl+C on the gateway's own console.</summary>
    /// <remarks>
    /// An idle gateway closes its databases within a second of the request. Fifteen seconds leaves
    /// room for slower devices and short in-flight work while keeping the whole stop well inside
    /// callers' command deadlines; a gateway still draining long work is terminated instead.
    /// </remarks>
    public static GatewayStopGrace ConsoleInterrupt { get; } =
        new(GatewayConsoleControl.TryInterrupt, TimeSpan.FromSeconds(15));

    /// <summary>Sends the request and waits for the tree to exit; never terminates anything.</summary>
    public void Request(WindowsKillOnCloseJob tree)
    {
        try
        {
            // Members stay pinned while the request is sent, so it reaches only this gateway's tree.
            if (tree.WithLiveMembers(members => members.Count > 0 && Interrupt(members)))
            {
                _ = tree.WaitForMembersToExit(Period);
            }
        }
        catch (Win32Exception)
        {
            // An unobservable tree gets no grace; the caller's termination still applies.
        }
    }
}
