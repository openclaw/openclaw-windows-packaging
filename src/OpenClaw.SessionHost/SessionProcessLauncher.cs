using System.Diagnostics;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// Starts a process from a launch request, preserving the argument vector
/// exactly.
/// </summary>
internal interface ISessionProcessLauncher
{
    /// <summary>Runs the request to completion and returns its exit code.</summary>
    int Run(SessionLaunchRequest request);

    /// <summary>
    /// Starts the request without waiting and returns the identity of the
    /// process that supervises it.
    /// </summary>
    SessionDetachedProcess Start(SessionLaunchRequest request, string helperPath);
}

/// <summary>
/// The supervising process left behind by a detached launch.
/// </summary>
/// <remarks>
/// The creation time is carried with the identifier because Windows reuses
/// process identifiers. An identifier alone would eventually name an unrelated
/// process, which the gateway would then claim and could be asked to stop.
/// </remarks>
internal sealed record SessionDetachedProcess(int ProcessId, DateTimeOffset StartTimeUtc);

/// <summary>
/// Launches the requested executable shell-free, so no quoting, metacharacter,
/// or <c>%VAR%</c> interpretation can alter the arguments.
/// </summary>
internal sealed class SessionProcessLauncher : ISessionProcessLauncher
{
    public int Run(SessionLaunchRequest request)
    {
        string workingDirectory = request.WorkingDirectory!;
        if (!Directory.Exists(workingDirectory))
        {
            // Falling back to another directory would run the caller's command
            // somewhere they never asked for, so this is fatal.
            throw new SessionLaunchException(
                $"The requested working directory does not exist: {workingDirectory}");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = request.Executable!,

            // No shell, and no stream redirection: the isolated session's
            // console handles are inherited so interactive and piped OpenClaw
            // behave as they do on the host.
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };

        foreach (string argument in request.Arguments!)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in request.Environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }
        PrependPath(startInfo, Path.GetDirectoryName(request.Executable));

        PrependPath(startInfo, request.PathPrefix);

        using Process process = new() { StartInfo = startInfo };
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

        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// Puts a directory at the front of the child's <c>PATH</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the guest can do this. The host supplies environment values that
    /// are merged onto this account's own environment, and it has no way to
    /// know what that account's <c>PATH</c> contains, so it names the directory
    /// and the resolution happens here.
    /// </para>
    /// <para>
    /// Prepending is the point: the packaged runtime has to win over any
    /// machine-wide Node.js for tools that resolve <c>node</c> or <c>npm</c> by
    /// name rather than by the path this package hands them.
    /// </para>
    /// </remarks>
    internal static void PrependPath(ProcessStartInfo startInfo, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        startInfo.Environment.TryGetValue("PATH", out string? inherited);
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(inherited)
            ? directory
            : $"{directory}{Path.PathSeparator}{inherited}";
    }

    /// <summary>
    /// Starts a detached launch by re-running this helper as a supervisor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The supervisor exists so that something owns the application's output.
    /// A detached process started directly would inherit the handles of the
    /// execution that launched it, and those close the moment that execution
    /// returns; the application would then be writing into a dead pipe. The
    /// supervisor instead opens the log itself and redirects the application
    /// into it.
    /// </para>
    /// <para>
    /// It is also the process whose identity is recorded. Ownership is then a
    /// claim about a process this package controls, and the listener check can
    /// ask whether the gateway port belongs to it or one of its descendants.
    /// </para>
    /// </remarks>
    public SessionDetachedProcess Start(SessionLaunchRequest request, string helperPath)
    {
        string requestPath = SessionSupervisor.RequestPathFor(request.StatusPath!);
        File.WriteAllText(requestPath, SessionLaunchProtocol.SerializeRequest(request));

        ProcessStartInfo startInfo = new()
        {
            FileName = helperPath,
            UseShellExecute = false,

            // No console of its own: at logon there is no desktop to show it on,
            // and a window would appear over whatever the user is doing.
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory!
        };

        startInfo.ArgumentList.Add("--supervise");
        startInfo.ArgumentList.Add(requestPath);

        Process process;
        try
        {
            using FileStream input = new(
                "NUL",
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using FileStream output = new(
                "NUL",
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            using FileStream error = new(
                "NUL",
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
            process = job.StartProcess(
                startInfo,
                input.SafeFileHandle,
                output.SafeFileHandle,
                error.SafeFileHandle,
                assignToJob: false);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            ArgumentException or
            IOException or
            InvalidOperationException or
            PlatformNotSupportedException)
        {
            throw new SessionLaunchException(
                $"Unable to start the gateway supervisor '{helperPath}': " +
                exception.Message);
        }

        using (process)
        {
            try
            {
                return new SessionDetachedProcess(process.Id, process.StartTime.ToUniversalTime());
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                // Without a creation time the process cannot be identified later,
                // and the listener check could claim an unrelated process.
                TryKill(process);
                throw new SessionLaunchException(
                    "The gateway supervisor could not be identified after it " +
                    $"started, so it was stopped: {exception.Message}");
            }
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
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
        }
    }
}
