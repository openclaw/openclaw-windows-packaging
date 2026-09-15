using System.Diagnostics;

namespace OpenClaw.Launcher;

internal static class GatewayLauncher
{
    public static async Task<int> RunAsync(
        string nodePath,
        string payloadDirectory,
        IReadOnlyList<string> openClawArguments,
        GatewayIsolationMode gatewayIsolationMode,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        ProcessStartInfo startInfo = CreateStartInfo(
            nodePath,
            payloadDirectory,
            openClawArguments,
            gatewayIsolationMode: gatewayIsolationMode);
        log?.Invoke("Launching OpenClaw with forwarded command arguments.");
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using Process process = job.StartProcess(startInfo);
        log?.Invoke($"OpenClaw child process started with PID {process.Id}.");

        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);
            log?.Invoke($"OpenClaw child process exited with code {process.ExitCode}.");
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string nodePath,
        string applicationDirectory,
        IReadOnlyList<string> openClawArguments,
        string? workingDirectory = null,
        GatewayIsolationMode gatewayIsolationMode =
            GatewayIsolationMode.Disabled)
    {
        string entryPoint = Path.Combine(applicationDirectory, "openclaw.mjs");
        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException(
                "The packaged OpenClaw entry point was not found.",
                entryPoint);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            // Default to the caller's directory, not applicationDirectory:
            // the package root is read-only, so OpenClaw's relative-path
            // writes need a writable working directory.
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        startInfo.Environment["OPENCLAW_SUPERVISOR_MODE"] = "external";
        startInfo.Environment["OPENCLAW_SERVICE_REPAIR_POLICY"] = "external";
        startInfo.Environment["OPENCLAW_NO_AUTO_UPDATE"] = "1";
        string? nodeDirectory = Path.GetDirectoryName(nodePath);
        if (!string.IsNullOrEmpty(nodeDirectory))
        {
            startInfo.Environment.TryGetValue("PATH", out string? inheritedPath);
            startInfo.Environment["PATH"] = string.IsNullOrEmpty(inheritedPath)
                ? nodeDirectory
                : $"{nodeDirectory}{Path.PathSeparator}{inheritedPath}";
        }
        startInfo.Environment["CLAWCTL_GATEWAY_ISOLATION"] =
            gatewayIsolationMode.ToEnvironmentValue();
        startInfo.ArgumentList.Add(entryPoint);

        foreach (string argument in openClawArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
