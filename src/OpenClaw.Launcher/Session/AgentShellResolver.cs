namespace OpenClaw.Launcher.Session;

/// <summary>The shell opened inside the agent session.</summary>
internal sealed record AgentShell(string ExecutablePath, string DisplayName);

/// <summary>Chooses the machine-wide PowerShell available to the agent.</summary>
internal static class AgentShellResolver
{
    internal static readonly string[] PowerShell7Paths =
    [
        @"C:\Program Files\PowerShell\7\pwsh.exe",
        @"C:\Program Files\PowerShell\7-preview\pwsh.exe",
    ];

    internal static string WindowsPowerShellPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    public static AgentShell Resolve(Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        foreach (string candidate in PowerShell7Paths)
        {
            if (fileExists(candidate))
            {
                return new AgentShell(candidate, "PowerShell 7");
            }
        }

        return new AgentShell(WindowsPowerShellPath, "Windows PowerShell");
    }

    public static IReadOnlyList<string> BuildArguments(
        string workspacePath,
        string agentName,
        string toolsDirectory,
        string nodeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);

        string prompt =
            "function prompt { " +
            $"Write-Host '[openclaw {Escape(agentName)}]' -NoNewline -ForegroundColor Cyan; " +
            "return ' ' + $ExecutionContext.SessionState.Path.CurrentLocation + '> ' }";
        string path =
            "$env:PATH = " +
            $"'{Escape(toolsDirectory)}' + [IO.Path]::PathSeparator + " +
            $"'{Escape(nodeDirectory)}' + [IO.Path]::PathSeparator + $env:PATH; ";
        string preparation =
            path +
            $"Set-Location -LiteralPath '{Escape(workspacePath)}' -ErrorAction SilentlyContinue; " +
            prompt;

        return ["-NoLogo", "-NoProfile", "-NoExit", "-Command", preparation];
    }

    private static string Escape(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
