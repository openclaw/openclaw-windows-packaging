using System.Diagnostics;
using System.Text;
using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayLauncherExecutionTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("throw 'fixture failure'", 1)]
    [InlineData("exit 23", 23)]
    public async Task TheLauncherCommandExitCodeReflectsWhetherTheActivationScriptRan(
        string? activationScript, int expectedExitCode)
    {
        // The launcher hands its recovery command to Windows PowerShell, and
        // that command is what decides the exit code. It runs here directly,
        // exactly as the launcher spells it, rather than through wscript.exe:
        // wscript.exe honors the machine's Windows Script Host policy, so on a
        // host that disables it (the case recovery now reports) it would show
        // a blocking dialog instead of running the fixture. The only child is
        // PowerShell running a fixture script, or none at all. No package,
        // alias, session, scheduled task, registry value, or user profile is
        // touched.
        string workingDirectory = Path.Combine(_root, "recovery %PATH% 日本語 owner's");
        Directory.CreateDirectory(workingDirectory);
        string activationPath = Path.Combine(workingDirectory, "activation.ps1");
        if (activationScript is not null)
        {
            File.WriteAllText(activationPath, activationScript, Encoding.Unicode);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in PowerShellArguments(
            GatewayLauncherScript.Create(workingDirectory, activationPath)))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using Process process = job.StartProcess(startInfo);

        // A hung child fails the test and is ended with the job, instead of
        // leaving the run waiting.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        // A missing activation script must not look like a successful task run.
        Assert.Equal(expectedExitCode, process.ExitCode);
    }

    // The launcher appends the PowerShell arguments to its command line in one
    // string literal. Reading them back from the generated launcher keeps this
    // test on the command the launcher really issues, flags included.
    private static string[] PowerShellArguments(string launcher)
    {
        const string prefix = "command += \"";
        const string suffix = "\";";
        string line = Assert.Single(
            launcher.Split("\r\n"),
            candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        Assert.EndsWith(suffix, line, StringComparison.Ordinal);

        return line[prefix.Length..^suffix.Length]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
