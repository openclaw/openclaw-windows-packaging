using System.Diagnostics;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The <c>--supervise</c> mode: owns one detached application and its log.
/// </summary>
/// <remarks>
/// This process is deliberately long-lived and is the identity the host
/// records. It is not a host-side supervisor: it runs inside the session
/// alongside the application, so nothing on the host has to stay alive for the
/// gateway to keep running.
/// </remarks>
internal static class SessionSupervisor
{
    public static string RequestPathFor(string statusPath) =>
        statusPath + ".request.json";

    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile)
    {
        SessionLaunchRequest request;
        try
        {
            request = SessionLaunchProtocol.ReadRequest(readFile(requestPath));
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            // Nothing is running yet and there is no console to complain to,
            // so there is nowhere to report this beyond the exit code.
            return SessionLaunchProtocol.HelperFailureExitCode;
        }

        string statusPath = request.StatusPath!;
        string logPath = request.LogPath!;

        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(logPath)
                ?? throw new SessionLaunchException(
                    $"The log path has no parent directory: {logPath}"));

            using FileStream log = new(
                logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using StreamWriter writer = new(log) { AutoFlush = true };

            return Supervise(request, statusPath, writer, writeFile);
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            WriteStatus(
                writeFile,
                statusPath,
                request,
                SessionSupervisorStatus.ExitedState,
                exception.Message);
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
        finally
        {
            TryDelete(requestPath);
        }
    }

    private static int Supervise(
        SessionLaunchRequest request,
        string statusPath,
        TextWriter log,
        Action<string, string> writeFile)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = request.Executable!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory!,

            // Redirected into the log this process owns. The application must
            // not inherit the handles this supervisor was started with: those
            // belong to the execution that launched it and close as soon as it
            // returns.
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in request.Arguments!)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in
            request.Environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using Process process = new() { StartInfo = startInfo };
        var pump = new object();
        process.OutputDataReceived += (_, e) => Write(lockObject: pump, log, e.Data);
        process.ErrorDataReceived += (_, e) => Write(lockObject: pump, log, e.Data);

        // Created before the application starts, so there is no window in which
        // a started application has no owner.
        using GuestKillOnCloseJob job = GuestKillOnCloseJob.Create();

        try
        {
            process.Start();
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            InvalidOperationException or
            PlatformNotSupportedException)
        {
            throw new SessionLaunchException(
                $"Unable to start '{request.Executable}': {exception.Message}");
        }

        try
        {
            job.Assign(process);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            // An application that could outlive its supervisor would keep its
            // listening port with nothing owning it, so an unjoinable process
            // is stopped rather than left running.
            TryKill(process);
            throw new SessionLaunchException(
                "The gateway could not be tied to its supervisor's lifetime, " +
                $"so it was stopped: {exception.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Written only once the application is actually running, so a status of
        // 'running' is evidence rather than intent.
        WriteStatus(
            writeFile,
            statusPath,
            request,
            SessionSupervisorStatus.RunningState,
            $"supervising process {process.Id}");

        process.WaitForExit();

        WriteStatus(
            writeFile,
            statusPath,
            request,
            SessionSupervisorStatus.ExitedState,
            $"the application exited with code {process.ExitCode}");
        return process.ExitCode;
    }

    private static void Write(object lockObject, TextWriter log, string? line)
    {
        if (line is null)
        {
            return;
        }

        // Both streams land in one file, so their writes are serialized to stop
        // a stdout and a stderr line interleaving into an unreadable one.
        lock (lockObject)
        {
            try
            {
                log.WriteLine(line);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException)
            {
                // A log that can no longer be written is not a reason to take
                // the gateway down.
            }
        }
    }

    private static void WriteStatus(
        Action<string, string> writeFile,
        string statusPath,
        SessionLaunchRequest request,
        string state,
        string detail)
    {
        try
        {
            writeFile(
                statusPath,
                SessionInspectProtocol.SerializeStatus(new SessionSupervisorStatus
                {
                    State = state,
                    ProcessId = System.Environment.ProcessId,
                    Port = 0,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Detail = detail
                }));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The status file is corroborating evidence, not the gateway's
            // reason to exist.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
