using System.Diagnostics;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The <c>--supervise</c> mode: owns a detached application and its restart handoffs.
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
        Action<string, string> writeFile,
        Func<IDisposable>? enterState = null)
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
            WriteStatus(writeFile, statusPath, request, "starting", "The supervisor accepted the launch.");
            enterState ??= SessionStateAccess.ForAgent().EnterReader;
            using IDisposable state = enterState();
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
        SessionProcessLauncher.PrependPath(startInfo, request.PathPrefix);
        SessionProcessLauncher.AppendNodeOptions(startInfo, request.NodeOptionsSuffix);

        // Held for the supervised process's whole lifetime, so setup cannot
        // reclaim the native root a running gateway resolves addons through.
        using FileStream? lease =
            SessionNativeStager.OpenConsumerLease(request.NativeRootPath);

        // The shared launcher creates suspended, assigns the job, then resumes.
        // Assigning after Process.Start leaves a window for orphan descendants.
        // Published by name so a stop that kills this supervisor can still pin and drain the gateway tree;
        // otherwise the kill releases the state lease while the tree is still dying.
        using Process self = Process.GetCurrentProcess();
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create(WindowsKillOnCloseJob.GatewayJobName(
            self.Id, self.StartTime.ToUniversalTime().Ticks));
        using var input = new FileStream("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // Re-exposing a FileStream's handle resets the native file position to
        // its cached position, overwriting earlier child output on a restart.
        var logHandle = log.SafeFileHandle;
        while (true)
        {
            int processId;
            int exitCode;
            // The named outer job stays open through handoff and replacement.
            // Each inner job drains the old generation before any successor.
            using (WindowsKillOnCloseJob generation = WindowsKillOnCloseJob.CreateContained(job))
            {
                using Process process = generation.StartProcess(
                    startInfo, input.SafeFileHandle, logHandle, logHandle);
                processId = process.Id;
                WriteStatus(
                    writeFile,
                    statusPath,
                    request,
                    SessionSupervisorStatus.RunningState,
                    $"supervising process {processId}");
                process.WaitForExit();
                exitCode = process.ExitCode;
                generation.Dispose();
                if (!generation.ExitConfirmed)
                {
                    throw new SessionLaunchException(
                        "The gateway process tree did not exit; no replacement was started.");
                }
            }

            string detail = $"the application exited with code {exitCode}";
            if (request.RestartHandoffArguments is not null)
            {
                WriteStatus(writeFile, statusPath, request, "starting",
                    $"{detail}; checking its restart handoff");
                GatewayRestartHandoffDecision decision =
                    SessionGatewayRestartHandoff.Consume(request, processId, job);
                if (decision.Restart)
                {
                    WriteStatus(writeFile, statusPath, request, "starting", decision.Detail);
                    continue;
                }
                detail += "; " + decision.Detail;
            }

            WriteStatus(writeFile, statusPath, request, SessionSupervisorStatus.ExitedState, detail);
            return exitCode;
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
            using Process self = Process.GetCurrentProcess();
            writeFile(
                statusPath,
                SessionInspectProtocol.SerializeStatus(new SessionSupervisorStatus
                {
                    RequestId = request.RequestId,
                    ProcessStartTimeUtc = self.StartTime.ToUniversalTime(),
                    HelperPath = System.Environment.ProcessPath,
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
