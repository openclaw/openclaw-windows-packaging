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
            return Supervise(request, statusPath, log, writeFile);
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
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
        FileStream log,
        Action<string, string> writeFile)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = request.Executable!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory!
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

        // The shared launcher creates suspended, assigns the job, then resumes.
        // Assigning after Process.Start leaves a window for orphan descendants.
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using var input = new FileStream("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Process process = job.StartProcess(startInfo, input.SafeFileHandle, log.SafeFileHandle, log.SafeFileHandle);

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
