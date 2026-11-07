using System.ComponentModel;
using System.Text;
using OpenClaw.GatewayRecovery;

namespace OpenClaw.Launcher.Tests.Recovery;

public sealed class GatewayRecoveryStartTests : IDisposable
{
    private const string PackageFamilyName = "OpenClaw.Gateway_test";

    private readonly string _root = TestDirectory.Create();
    private readonly List<string> _log = [];

    public GatewayRecoveryStartTests()
    {
        Directory.CreateDirectory(PackageDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Package.appxmanifest"),
            Path.Combine(PackageDirectory, "AppxManifest.xml"));
    }

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

    private string PackageDirectory => Path.Combine(_root, "package");

    private string LocalAppData => Path.Combine(_root, "local");

    private string WorkingDirectory => Path.Combine(_root, "profile");

    private GatewayRecoveryEnvironment Environment(string? packageFamilyName = PackageFamilyName) =>
        new(packageFamilyName, PackageDirectory, LocalAppData, WorkingDirectory);

    private string CreateAlias(string alias)
    {
        string path = Path.Combine(LocalAppData, "Microsoft", "WindowsApps", PackageFamilyName, alias);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture alias");
        return path;
    }

    [Fact]
    public void TheGatewayStartsThroughThisPackagesControlAliasAndKeepsItsExitCode()
    {
        // The task's last-run result is the recovery result, not whether the
        // helper itself started.
        string control = CreateAlias("clawctl.exe");
        var runner = new RecordingRunner(exitCode: 23);

        int exitCode = GatewayRecoveryStart.Run(Environment(), runner, _log.Add);

        Assert.Equal(23, exitCode);
        Assert.Equal((control, "gateway-service start --recovery", WorkingDirectory), Assert.Single(runner.Runs));
        Assert.Contains(_log, line => line.Contains(control, StringComparison.Ordinal));
        Assert.Contains(_log, line => line.EndsWith("exited with code 23.", StringComparison.Ordinal));
    }

    [Fact]
    public void ASideBySideIdentityStartsItsOwnControlAlias()
    {
        string manifestPath = Path.Combine(PackageDirectory, "AppxManifest.xml");
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace(
                "Alias=\"clawctl.exe\"",
                "Alias=\"clawctl-foo.exe\"",
                StringComparison.Ordinal));
        CreateAlias("clawctl.exe");
        string own = CreateAlias("clawctl-foo.exe");
        var runner = new RecordingRunner(exitCode: 0);

        Assert.Equal(0, GatewayRecoveryStart.Run(Environment(), runner, _log.Add));
        Assert.Equal(own, Assert.Single(runner.Runs).Path);
    }

    [Fact]
    public void OutsideThePackageNothingStartsAndTheRunFails()
    {
        var runner = new RecordingRunner(exitCode: 0);

        int exitCode = GatewayRecoveryStart.Run(Environment(packageFamilyName: null), runner, _log.Add);

        Assert.Equal(GatewayRecoveryStart.FailureExitCode, exitCode);
        Assert.Empty(runner.Runs);
        Assert.Contains(_log, line => line.Contains("not running from the installed package", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingControlAliasFailsTheSignInRunInsteadOfReportingSuccess()
    {
        var runner = new RecordingRunner(exitCode: 0);

        int exitCode = GatewayRecoveryStart.Run(Environment(), runner, _log.Add);

        Assert.Equal(GatewayRecoveryStart.FailureExitCode, exitCode);
        Assert.Empty(runner.Runs);
        Assert.Contains(_log, line =>
            line.Contains("clawctl.exe", StringComparison.Ordinal) &&
            line.Contains("clawctl setup", StringComparison.Ordinal));
    }

    [Fact]
    public void AManifestWithoutAUsableControlApplicationFailsTheRun()
    {
        File.WriteAllText(Path.Combine(PackageDirectory, "AppxManifest.xml"), "<Package />");
        CreateAlias("clawctl.exe");
        var runner = new RecordingRunner(exitCode: 0);

        int exitCode = GatewayRecoveryStart.Run(Environment(), runner, _log.Add);

        Assert.Equal(GatewayRecoveryStart.FailureExitCode, exitCode);
        Assert.Empty(runner.Runs);
        Assert.Contains(_log, line => line.Contains("'Control' application", StringComparison.Ordinal));
    }

    [Fact]
    public void AControlAliasThatCannotStartIsLoggedWithItsError()
    {
        CreateAlias("clawctl.exe");
        var runner = new RecordingRunner(exitCode: 0)
        {
            Failure = new Win32Exception(2)
        };

        int exitCode = GatewayRecoveryStart.Run(Environment(), runner, _log.Add);

        Assert.Equal(GatewayRecoveryStart.FailureExitCode, exitCode);
        Assert.Contains(_log, line => line.Contains("(error 2)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRunnerStartsTheChildWithoutAConsoleInTheGivenDirectoryAndReturnsItsExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The child is a fixture script; no package, alias, session, or
        // scheduled task is read or modified.
        string directory = Path.Combine(_root, "working %PATH% 日本語");
        Directory.CreateDirectory(directory);
        string script = Path.Combine(_root, "fixture.ps1");
        File.WriteAllText(script, """
            $ErrorActionPreference = 'Stop'
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            public static class FixtureConsole {
                [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
            }
            '@
            if ([FixtureConsole]::GetConsoleWindow() -ne [IntPtr]::Zero) { exit 99 }
            [IO.File]::WriteAllText((Join-Path (Get-Location).Path 'observed.txt'), (Get-Location).Path)
            exit 23
            """, Encoding.Unicode);

        int exitCode = new HiddenProcessRunner().Run(
            Path.Combine(System.Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"",
            directory);

        Assert.Equal(23, exitCode);
        Assert.Equal(directory, File.ReadAllText(Path.Combine(directory, "observed.txt")));
    }

    private sealed class RecordingRunner(int exitCode) : IHiddenProcessRunner
    {
        public List<(string Path, string Arguments, string WorkingDirectory)> Runs { get; } = [];

        public Exception? Failure { get; init; }

        public int Run(string path, string arguments, string workingDirectory)
        {
            Runs.Add((path, arguments, workingDirectory));
            return Failure is null ? exitCode : throw Failure;
        }
    }
}
