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

}

/// <summary>
/// The supervising process left behind by a detached launch.
/// </summary>
/// <remarks>
/// The creation time is carried with the identifier because Windows reuses
/// process identifiers. An identifier alone would eventually name an unrelated
/// process, which the gateway would then claim and could be asked to stop.
/// </remarks>
/// <summary>
/// Launches the requested executable shell-free, so no quoting, metacharacter,
/// or <c>%VAR%</c> interpretation can alter the arguments.
/// </summary>
internal sealed class SessionProcessLauncher : ISessionProcessLauncher
{
    internal static void PrependPath(ProcessStartInfo startInfo, string? runtimeDirectory)
    {
        if (string.IsNullOrEmpty(runtimeDirectory))
        {
            return;
        }

        string inheritedPath = startInfo.Environment["PATH"] ?? string.Empty;
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(inheritedPath)
            ? runtimeDirectory
            : $"{runtimeDirectory};{inheritedPath}";
    }

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

}
