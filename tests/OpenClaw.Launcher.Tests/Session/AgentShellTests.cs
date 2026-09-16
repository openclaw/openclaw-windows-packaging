using System.Diagnostics;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class AgentShellTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public void PowerShell7IsPreferredBeforePreviewAndWindowsPowerShell()
    {
        AgentShell shell = AgentShellResolver.Resolve(
            path => path == AgentShellResolver.PowerShell7Paths[0] ||
                path == AgentShellResolver.PowerShell7Paths[1]);

        Assert.Equal(AgentShellResolver.PowerShell7Paths[0], shell.ExecutablePath);
        Assert.Equal("PowerShell 7", shell.DisplayName);
    }

    [Fact]
    public void WindowsPowerShellIsUsedWhenNoMachineWidePowerShell7Exists()
    {
        AgentShell shell = AgentShellResolver.Resolve(_ => false);

        Assert.Equal(AgentShellResolver.WindowsPowerShellPath, shell.ExecutablePath);
        Assert.Equal("Windows PowerShell", shell.DisplayName);
    }

    [Fact]
    public async Task ProductionPowerShellPathStartsSuccessfully()
    {
        AgentShell shell = AgentShellResolver.Resolve(File.Exists);
        Assert.True(File.Exists(shell.ExecutablePath), shell.ExecutablePath);

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add("exit 0");

        Assert.True(process.Start());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void ShellPreparationPutsToolsThenAgentNodeAheadOfExistingPath()
    {
        IReadOnlyList<string> arguments = AgentShellResolver.BuildArguments(
            @"C:\shared workspace",
            "agent's name",
            @"C:\shared workspace\.openclaw-tools",
            @"C:\Users\agent\AppData\Local\OpenClaw\NodeJS\node-v24");

        Assert.Equal(["-NoLogo", "-NoProfile", "-NoExit", "-Command"], arguments.Take(4));
        string preparation = arguments[4];
        Assert.Contains(
            @"C:\shared workspace\.openclaw-tools' + [IO.Path]::PathSeparator + 'C:\Users\agent",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains("agent''s name", preparation, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
