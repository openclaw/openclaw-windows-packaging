using System.Diagnostics;

namespace OpenClaw.GatewayRecovery;

/// <summary>
/// Starts a console program without a console window. This process has no
/// console of its own, so the child would otherwise get a new, visible one,
/// which Windows Terminal turns into a terminal window when it is the default.
/// </summary>
internal sealed class HiddenProcessRunner : IHiddenProcessRunner
{
    public int Run(string path, string arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using Process process = Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        }) ?? throw new InvalidOperationException($"'{path}' did not start a process.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
