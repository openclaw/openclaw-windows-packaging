using System.Diagnostics;
using System.Text;
using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayPersistenceManagerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private readonly FakeGatewayTaskScheduler _scheduler = new();

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

    private string StateRoot => Path.Combine(_root, "state");

    private string StartupFolder => Path.Combine(_root, "startup");

    private string LauncherPath => Path.Combine(StateRoot, "gateway-launcher.js");

    private string ActivationScriptPath => Path.ChangeExtension(LauncherPath, ".ps1");

    private GatewayPersistenceManager CreateManager(
        Func<string, string?>? resolveUserSid = null,
        Action<string>? deleteFile = null,
        Action<string>? log = null) =>
        new(
            _scheduler,
            new GatewayPersistenceOptions(
                UserSid: "S-1-5-21-1",
                PackageFamilyName: "OpenClaw.Gateway_test",
                LauncherPath: LauncherPath,
                StartupFolderPath: StartupFolder,
                WorkingDirectory: StateRoot,
                ScriptHostPath: @"C:\Windows\System32\wscript.exe",
                DeleteFile: deleteFile),
            log,
            resolveUserSid: resolveUserSid);

    private GatewayTaskSnapshot DesiredSnapshot() =>
        GatewayTaskDefinition.CreateSnapshot(
            "S-1-5-21-1",
            @"C:\Windows\System32\wscript.exe",
            LauncherPath);

    [Theory]
    [InlineData(@"C:\outside\gateway-launcher.js")]
    [InlineData(@"state\child\gateway-launcher.js")]
    public void RejectsLauncherPathOutsideTheStateRoot(string launcherPath)
    {
        GatewayPersistenceOptions options = new(
            UserSid: "S-1-5-21-1",
            PackageFamilyName: "OpenClaw.Gateway_test",
            LauncherPath: Path.IsPathRooted(launcherPath)
                ? launcherPath
                : Path.Combine(_root, launcherPath),
            StartupFolderPath: StartupFolder,
            WorkingDirectory: StateRoot,
            ScriptHostPath: @"C:\Windows\System32\wscript.exe");

        Assert.Throws<ArgumentException>(() =>
            new GatewayPersistenceManager(_scheduler, options));
    }

    [Fact]
    public async Task InstallingWritesTheLauncherAndRegistersTheTask()
    {
        GatewayPersistenceManager manager = CreateManager();

        GatewayPersistenceInstallResult result =
            await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.Equal(GatewayPersistenceLane.TaskScheduler, result.Lane);
        Assert.True(result.Changed);
        Assert.True(File.Exists(LauncherPath));
        Assert.True(File.Exists(ActivationScriptPath));
        Assert.Contains($"register:{manager.TaskName}", _scheduler.Calls);
    }

    [Fact]
    public async Task SuccessfulRegistrationWithTriggerDriftDoesNotClaimRecoveryIsReady()
    {
        _scheduler.RegistrationProbe = GatewayTaskProbe.Present(DesiredSnapshot() with
        {
            LogonTriggerUserId = "S-1-5-21-2"
        });
        GatewayPersistenceManager manager = CreateManager();
        Directory.CreateDirectory(StartupFolder);
        File.WriteAllText(manager.FallbackPath, "existing fallback");

        var result = await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, result.State);
        Assert.Contains("Expected 'S-1-5-21-1', observed 'S-1-5-21-2'", result.Detail, StringComparison.Ordinal);
        Assert.Equal(GatewayPersistenceManager.RepairCommand, result.Remediation);
        Assert.True(File.Exists(manager.FallbackPath));
        Assert.Single(_scheduler.Calls, call => call.StartsWith("register:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulRegistrationWithUnreadableResultRemainsUnknown()
    {
        _scheduler.RegistrationProbe = GatewayTaskProbe.Unreadable("Access is denied.");

        var result = await CreateManager().InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Unknown, result.State);
        Assert.Contains("Access is denied.", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairReplacesWrongTriggerAndStatusConfirmsTheRegisteredIdentity()
    {
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot() with
        {
            LogonTriggerUserId = "S-1-5-21-2"
        });
        var manager = CreateManager();

        var installed = await manager.InstallAsync(CancellationToken.None);
        var status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, installed.State);
        Assert.Equal(GatewayPersistenceState.Ready, status.State);
        Assert.Equal("S-1-5-21-1", _scheduler.Probe.Snapshot!.LogonTriggerUserId);
    }

    [Fact]
    public async Task TheRegisteredActionRunsTheLauncherNotTheAliasDirectly()
    {
        // The indirection is the point: the launcher can be rewritten
        // unelevated, whereas changing the task's own action needs another
        // registration and another consent prompt.
        await CreateManager().InstallAsync(CancellationToken.None);

        Assert.NotNull(_scheduler.RegisteredXml);
        Assert.Contains(LauncherPath, _scheduler.RegisteredXml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "clawctl.exe",
            _scheduler.RegisteredXml,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheGeneratedLauncherActivatesTheOwningPackageControlApplication()
    {
        await CreateManager().InstallAsync(CancellationToken.None);

        string launcher = await File.ReadAllTextAsync(LauncherPath, CancellationToken.None);
        string activation = await File.ReadAllTextAsync(ActivationScriptPath, CancellationToken.None);

        Assert.Contains("shell.Run(command, 0, true)", launcher, StringComparison.Ordinal);
        Assert.Contains("$applicationId = 'Control'", activation, StringComparison.Ordinal);
        Assert.Contains("$start.CreateNoWindow = $true", activation, StringComparison.Ordinal);
        Assert.Contains(
            "gateway-service start --recovery",
            activation,
            StringComparison.Ordinal);
        Assert.Contains("Microsoft\\WindowsApps\\", activation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyActivationUpgradeDoesNotOverwriteModifiedContent()
    {
        await CreateManager().InstallAsync(CancellationToken.None);
        string modified =
            (await File.ReadAllTextAsync(
                ActivationScriptPath,
                CancellationToken.None))
            .Replace(
                GatewayLauncherScript.ControlArguments,
                "gateway-service start",
                StringComparison.Ordinal) +
            "# user edit";
        await File.WriteAllTextAsync(
            ActivationScriptPath,
            modified,
            CancellationToken.None);

        bool legacyInvocation =
            GatewayLauncherScript.UpgradeLegacyActivationScript(
                ActivationScriptPath,
                "OpenClaw.Gateway_test",
                _ => { });

        Assert.False(legacyInvocation);
        Assert.Equal(
            modified,
            await File.ReadAllTextAsync(
                ActivationScriptPath,
                CancellationToken.None));
    }

    [Fact]
    public async Task TheControlActivationScriptFailsClosedOnMissingOrMismatchedTargets()
    {
        await CreateManager().InstallAsync(CancellationToken.None);

        string activation = await File.ReadAllTextAsync(ActivationScriptPath, CancellationToken.None);

        Assert.Contains("PackageFamilyName -eq $packageFamilyName", activation, StringComparison.Ordinal);
        Assert.Contains("Get-AppxPackage -Name 'OpenClaw.Gateway' |", activation, StringComparison.Ordinal);
        Assert.Contains("does not declare application '$applicationId'", activation, StringComparison.Ordinal);
        Assert.Contains("does not target openclaw.exe", activation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "OpenClawFoundation.OpenClawGateway_rfcbke2p71se2",
        "OpenClawFoundation.OpenClawGateway")]
    [InlineData(
        "OpenClawFoundation.OpenClawGateway-foo_rfcbke2p71se2",
        "OpenClawFoundation.OpenClawGateway-foo")]
    public void TheControlActivationScriptLooksUpTheIdentityThatGeneratedIt(
        string packageFamilyName,
        string packageName)
    {
        // A side-by-side identity's logon recovery must find its own package;
        // looking up the base name would report it as not registered.
        string activation =
            GatewayLauncherScript.CreateActivationScript(packageFamilyName);

        Assert.Contains(
            $"$package = Get-AppxPackage -Name '{packageName}' | ",
            activation,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMatchingRegistrationIsNotRewritten()
    {
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot());
        GatewayPersistenceManager manager = CreateManager();

        GatewayPersistenceInstallResult result =
            await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.DoesNotContain(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAccountNameTriggerResolvingToTheOwnerSidIsNotRewritten()
    {
        GatewayTaskSnapshot scheduledTask = DesiredSnapshot() with
        {
            LogonTriggerUserId = @"CONTOSO\agent",
        };
        _scheduler.Probe = GatewayTaskProbe.Present(scheduledTask);

        GatewayPersistenceInstallResult result = await CreateManager(
            account => account == @"CONTOSO\agent" ? "S-1-5-21-1" : null)
            .InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.DoesNotContain(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnresolvableAccountNameTriggerIsRewritten()
    {
        GatewayTaskSnapshot scheduledTask = DesiredSnapshot() with
        {
            LogonTriggerUserId = @"CONTOSO\former-agent",
        };
        _scheduler.Probe = GatewayTaskProbe.Present(scheduledTask);

        GatewayPersistenceInstallResult result = await CreateManager(_ => null)
            .InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.Contains(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATaskPointingAtAnOldPackageVersionIsRewritten()
    {
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot() with
        {
            Command = Path.Combine(_root, "old-package", "openclaw.exe"),
            Arguments = "gateway-service start",
        });

        GatewayPersistenceInstallResult result =
            await CreateManager().InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.Contains(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreadableProbeLeavesTheRegistrationAlone()
    {
        // Re-registering on a refused read is an unbounded retry: the write is
        // as likely to be refused as the read was.
        _scheduler.Probe = GatewayTaskProbe.Unreadable("Access is denied.");

        GatewayPersistenceInstallResult result =
            await CreateManager().InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Unknown, result.State);
        Assert.DoesNotContain(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
        Assert.Equal(GatewayPersistenceManager.RepairCommand, result.Remediation);
    }

    [Fact]
    public async Task AFailedRegistrationFallsBackToTheStartupFolderAndSaysSo()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();

        GatewayPersistenceInstallResult result =
            await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.Equal(GatewayPersistenceLane.StartupFolderFallback, result.Lane);
        Assert.True(File.Exists(manager.FallbackPath));
        Assert.Contains("Access is denied.", result.Detail, StringComparison.Ordinal);
    }

    // Setup reports the degraded lane on the console; the bundle must still
    // record why the preferred logon task was refused.
    [Fact]
    public async Task AFailedRegistrationIsLoggedWithTheSchedulersReason()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        List<string> log = [];

        await CreateManager(log: log.Add).InstallAsync(CancellationToken.None);

        Assert.Contains(
            log,
            line => line.EndsWith(
                "could not be registered; trying the Startup-folder fallback: Access is denied.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFallbackCallsTheSameLauncherAsTheTask()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();

        await manager.InstallAsync(CancellationToken.None);

        Assert.Contains(
            LauncherPath,
            await File.ReadAllTextAsync(
                manager.FallbackPath,
                CancellationToken.None),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallbackStatusRequiresAnUnmodifiedLauncher()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);
        await File.WriteAllTextAsync(
            LauncherPath,
            GatewayLauncherScript.Create(StateRoot, "stale.ps1"),
            CancellationToken.None);

        GatewayPersistenceStatus status =
            await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.NotInstalled, status.State);
    }

    [Fact]
    public async Task FallbackStatusRejectsAGeneratedMarkerWithARedirectedTarget()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);
        await File.WriteAllTextAsync(
            manager.FallbackPath,
            GatewayLauncherScript.CreateFallback(Path.Combine(_root, "redirected.cmd")),
            CancellationToken.None);

        GatewayPersistenceStatus status =
            await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.NotInstalled, status.State);
    }

    [Fact]
    public async Task GeneratedScriptFilesUseTheirHostEncoding()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();

        await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(Encoding.Unicode.GetPreamble(), (await File.ReadAllBytesAsync(LauncherPath))[..2]);
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, (await File.ReadAllBytesAsync(manager.FallbackPath))[..3]);
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF },
            (await File.ReadAllBytesAsync(ActivationScriptPath))[..3]);
    }

    [Fact]
    public async Task ASucceedingRegistrationRemovesAPreviousFallback()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);

        _scheduler.RegisterResult = GatewayTaskOperation.Success;
        await manager.InstallAsync(CancellationToken.None);

        Assert.False(File.Exists(manager.FallbackPath));
    }

    [Fact]
    public async Task StatusReportsNotInstalledWithoutRegisteringAnything()
    {
        GatewayPersistenceManager manager = CreateManager();

        GatewayPersistenceStatus status =
            await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.NotInstalled, status.State);
        Assert.Equal(GatewayPersistenceLane.None, status.Lane);
        Assert.DoesNotContain(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
        Assert.False(File.Exists(LauncherPath));
    }

    [Fact]
    public async Task StatusReportsAnUnreadableProbeAsUnknown()
    {
        _scheduler.Probe = GatewayTaskProbe.Unreadable("Access is denied.");

        GatewayPersistenceStatus status =
            await CreateManager().GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Unknown, status.State);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("battery")]
    [InlineData("elevated")]
    [InlineData("other-user")]
    [InlineData("time-limit")]
    [InlineData("other-command")]
    public async Task StatusReportsDriftWithTheCommandThatRepairsIt(string kind)
    {
        await CreateManager().InstallAsync(CancellationToken.None);
        GatewayTaskSnapshot desired = DesiredSnapshot();
        _scheduler.Probe = GatewayTaskProbe.Present(kind switch
        {
            "disabled" => desired with { Enabled = false },
            "battery" => desired with { DisallowStartIfOnBatteries = true },
            "elevated" => desired with { RunLevel = "HighestAvailable" },
            "other-user" => desired with { UserId = "S-1-5-21-999" },
            "time-limit" => desired with { ExecutionTimeLimit = "PT72H" },
            _ => desired with { Command = @"C:\Windows\System32\notepad.exe" }
        });

        GatewayPersistenceStatus status =
            await CreateManager().GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, status.State);
        Assert.Equal(GatewayPersistenceManager.RepairCommand, status.Remediation);
        Assert.NotNull(status.Detail);
    }

    [Fact]
    public async Task AMissingLauncherIsReportedEvenWhenTheTaskIsCorrect()
    {
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);
        File.Delete(LauncherPath);
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot());

        GatewayPersistenceStatus status =
            await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, status.State);
    }

    [Fact]
    public async Task AnInstalledInstallationIsReportedAsReady()
    {
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot());

        GatewayPersistenceStatus status =
            await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, status.State);
        Assert.Equal(GatewayPersistenceLane.TaskScheduler, status.Lane);
        Assert.Null(status.Detail);
    }

    [Fact]
    public async Task UninstallingRemovesTheTaskLauncherAndFallback()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);

        GatewayPersistenceRemovalResult result =
            await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.False(File.Exists(LauncherPath));
        Assert.False(File.Exists(ActivationScriptPath));
        Assert.False(File.Exists(manager.FallbackPath));
        Assert.Contains($"delete:{manager.TaskName}", _scheduler.Calls);
    }

    [Fact]
    public async Task UninstallingLeavesAFileThisInstallationDidNotGenerate()
    {
        GatewayPersistenceManager manager = CreateManager();
        Directory.CreateDirectory(StartupFolder);
        await File.WriteAllTextAsync(
            manager.FallbackPath,
            "@echo someone else's script",
            CancellationToken.None);

        await manager.UninstallAsync(CancellationToken.None);

        Assert.True(File.Exists(manager.FallbackPath));
    }

    [Fact]
    public async Task InstallingLeavesALauncherThisInstallationDidNotGenerate()
    {
        GatewayPersistenceManager manager = CreateManager();
        Directory.CreateDirectory(StateRoot);
        await File.WriteAllTextAsync(
            LauncherPath,
            "@echo someone else's launcher",
            CancellationToken.None);

        GatewayPersistenceInstallResult result =
            await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, result.State);
        Assert.Equal(
            "@echo someone else's launcher",
            await File.ReadAllTextAsync(LauncherPath, CancellationToken.None));
        Assert.DoesNotContain(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UninstallingWithNoArtifactsIsIdempotent()
    {
        GatewayPersistenceRemovalResult result =
            await CreateManager().UninstallAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task AFailedDeletionIsReportedRatherThanClaimedAsSuccess()
    {
        _scheduler.DeleteResult = GatewayTaskOperation.Failure("Access is denied.");

        GatewayPersistenceRemovalResult result =
            await CreateManager().UninstallAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Access is denied.", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UninstallingReportsGeneratedArtifactDeletionFailure(bool fallback)
    {
        string fallbackPath = Path.Combine(
            StartupFolder,
            "OpenClaw Gateway OpenClaw.Gateway_test.cmd");
        GatewayPersistenceManager manager = CreateManager(deleteFile: path =>
        {
            if (string.Equals(path, fallback ? fallbackPath : LauncherPath,
                StringComparison.Ordinal))
            {
                throw new IOException("The generated file is locked.");
            }

            File.Delete(path);
        });
        string target = fallback ? fallbackPath : LauncherPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(
            target,
            GatewayLauncherScript.Create(StateRoot, "clawctl.exe"));

        GatewayPersistenceRemovalResult result =
            await manager.UninstallAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("locked", result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task AGeneratedLauncherThatCannotBeDeletedIsReported()
    {
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);

        using (FileStream handle = new(
            LauncherPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            GatewayPersistenceRemovalResult result =
                await manager.UninstallAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.False(result.Succeeded);
            Assert.Contains(LauncherPath, result.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ThePackageActivationScriptCanChangeWithoutReRegisteringTheTask()
    {
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot());
        string before = _scheduler.RegisteredXml!;

        await File.WriteAllTextAsync(
            ActivationScriptPath,
            GatewayLauncherScript.CreateActivationScript("OpenClaw.Gateway_stale"),
            CancellationToken.None);
        GatewayPersistenceInstallResult result =
            await manager.InstallAsync(CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.Equal(before, _scheduler.RegisteredXml);
        Assert.Contains(
            "$packageFamilyName = 'OpenClaw.Gateway_test'",
            await File.ReadAllTextAsync(
                ActivationScriptPath,
                CancellationToken.None),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryLaunchesHiddenAndReturnsTheChildExitCode(bool fallback)
    {
        // The only child is a fixture PowerShell script. No package, alias,
        // session, scheduled task, or user profile is read or modified.
        string workingDirectory = Path.Combine(_root, "recovery %PATH% 日本語");
        Directory.CreateDirectory(workingDirectory);
        string activationPath = Path.Combine(workingDirectory, "fixture.ps1");
        string launcherPath = Path.Combine(workingDirectory, "launcher.js");
        string fallbackPath = Path.Combine(workingDirectory, "fallback.wsf");
        File.WriteAllText(activationPath, """
            $ErrorActionPreference = 'Stop'
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            public static class FixtureConsole {
                [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
                [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
            }
            '@
            $window = [FixtureConsole]::GetConsoleWindow()
            if ($window -ne [IntPtr]::Zero -and [FixtureConsole]::IsWindowVisible($window)) { exit 99 }
            [IO.File]::WriteAllText((Join-Path (Get-Location).Path 'observed.txt'), (Get-Location).Path)
            exit 23
            """, Encoding.Unicode);
        File.WriteAllText(
            launcherPath,
            GatewayLauncherScript.Create(workingDirectory, activationPath),
            Encoding.Unicode);
        File.WriteAllText(
            fallbackPath,
            GatewayLauncherScript.CreateFallback(launcherPath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "wscript.exe"),
            UseShellExecute = false,
            ArgumentList = { "//B", "//Nologo", fallback ? fallbackPath : launcherPath }
        })!;
        process.WaitForExit();

        Assert.Equal(23, process.ExitCode);
        Assert.Equal(workingDirectory, File.ReadAllText(Path.Combine(workingDirectory, "observed.txt")));
    }

    [Theory]
    [InlineData("other.exe", "clawctl.exe", "does not target openclaw.exe")]
    [InlineData("openclaw.exe", "openclaw.exe", "unexpected control alias")]
    [InlineData("openclaw.exe", "../clawctl.exe", "unexpected control alias")]
    public void RecoveryRejectsInvalidPackageTargetsBeforeLaunching(
        string executable, string alias, string expectedError)
    {
        string manifestPath = Path.Combine(_root, "AppxManifest.xml");
        File.WriteAllText(manifestPath, $"""
            <Package><Applications><Application Id="Control" Executable="{executable}">
            <Extensions><Extension Category="windows.appExecutionAlias"><AppExecutionAlias>
            <ExecutionAlias Alias="{alias}" /></AppExecutionAlias></Extension></Extensions>
            </Application></Applications></Package>
            """);
        string scriptPath = Path.Combine(_root, "invalid-target.ps1");
        string fixtureRoot = _root.Replace("'", "''", StringComparison.Ordinal);
        File.WriteAllText(scriptPath,
            $"function Get-AppxPackage {{ [pscustomobject]@{{ PackageFamilyName = 'OpenClaw.Gateway_test'; Status = 'Ok'; InstallLocation = '{fixtureRoot}' }} }}\n" +
            GatewayLauncherScript.CreateActivationScript("OpenClaw.Gateway_test"),
            Encoding.Unicode);
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }
        })!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(1, process.ExitCode);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedLegacyCleanupIsReportedWithARepairCommand()
    {
        GatewayPersistenceManager manager = CreateManager(deleteFile: path =>
        {
            if (Path.GetExtension(path) == ".cmd")
            {
                throw new IOException("legacy file is locked");
            }
            File.Delete(path);
        });
        Directory.CreateDirectory(StateRoot);
        await File.WriteAllTextAsync(Path.ChangeExtension(LauncherPath, ".cmd"), GatewayLauncherScript.Marker);

        GatewayPersistenceInstallResult result = await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, result.State);
        Assert.Contains("legacy file is locked", result.Detail, StringComparison.Ordinal);
        Assert.Equal(GatewayPersistenceManager.RepairCommand, result.Remediation);
        Assert.True(File.Exists(Path.ChangeExtension(LauncherPath, ".cmd")));
    }

    [Fact]
    public async Task ReinstallMigratesOnlyGeneratedCmdFiles()
    {
        GatewayPersistenceManager manager = CreateManager();
        Directory.CreateDirectory(StateRoot);
        Directory.CreateDirectory(StartupFolder);
        string legacyLauncher = Path.ChangeExtension(LauncherPath, ".cmd");
        string legacyFallback = Path.ChangeExtension(manager.FallbackPath, ".cmd");
        await File.WriteAllTextAsync(legacyLauncher, GatewayLauncherScript.Marker);
        await File.WriteAllTextAsync(legacyFallback, GatewayLauncherScript.Marker);
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot() with
        {
            Command = @"C:\Windows\System32\cmd.exe",
            Arguments = $"/d /c \"\"{legacyLauncher}\"\""
        });

        Assert.Equal(GatewayPersistenceState.ActionRequired,
            (await manager.GetStatusAsync(CancellationToken.None)).State);
        GatewayPersistenceInstallResult result = await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.False(File.Exists(legacyLauncher));
        Assert.False(File.Exists(legacyFallback));
        Assert.True(GatewayTaskDefinition.TryParse(_scheduler.RegisteredXml!, out GatewayTaskSnapshot? registered, out _));
        Assert.Equal(DesiredSnapshot(), registered);

        await File.WriteAllTextAsync(legacyFallback, "user-owned file");
        await manager.UninstallAsync(CancellationToken.None);
        Assert.Equal("user-owned file", await File.ReadAllTextAsync(legacyFallback));
        Assert.False(File.Exists(LauncherPath));
        Assert.False(File.Exists(ActivationScriptPath));
    }

    [Fact]
    public async Task TheLauncherEntersAControlledWorkingDirectory()
    {
        // At logon the task's working directory is the system directory.
        await CreateManager().InstallAsync(CancellationToken.None);

        Assert.Contains(
            $"shell.CurrentDirectory = \"{StateRoot.Replace("\\", "\\\\", StringComparison.Ordinal)}\";",
            await File.ReadAllTextAsync(
                LauncherPath,
                CancellationToken.None),
            StringComparison.Ordinal);
    }


    [Theory]
    [InlineData("")]
    [InlineData("<Exec><Command>C:\\Windows\\System32\\cmd.exe</Command><Arguments>/d /c \"\"x\"\"</Arguments></Exec><Exec><Command>C:\\Windows\\System32\\cmd.exe</Command></Exec>")]
    [InlineData("<ComHandler><ClassId>{00000000-0000-0000-0000-000000000000}</ClassId></ComHandler><Exec><Command>C:\\Windows\\System32\\cmd.exe</Command><Arguments>/d /c \"\"x\"\"</Arguments></Exec>")]
    [InlineData("<ComHandler><ClassId>{00000000-0000-0000-0000-000000000000}</ClassId></ComHandler>")]
    public void TaskParsingRequiresExactlyOneActionAndItMustBeExec(string actions)
    {
        string xml = ReplaceActions(
            GatewayTaskDefinition.CreateXml(DesiredSnapshot(), "fixture"),
            actions);

        Assert.True(GatewayTaskDefinition.TryParse(
            xml,
            out GatewayTaskSnapshot? snapshot,
            out string? detail), detail);
        Assert.False(snapshot!.HasSingleExecAction);
    }

    [Fact]
    public async Task InstallingReRegistersATaskWithMixedExecAndComHandlerActions()
    {
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot() with
        {
            HasSingleExecAction = false
        });

        GatewayPersistenceInstallResult result =
            await CreateManager().InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.Contains(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
    }

    private static string ReplaceActions(string xml, string actions)
    {
        const string start = "<Actions Context=\"Author\">";
        const string end = "</Actions>";
        int startIndex = xml.IndexOf(start, StringComparison.Ordinal);
        int contentStart = startIndex + start.Length;
        int endIndex = xml.IndexOf(end, contentStart, StringComparison.Ordinal);
        return xml[..contentStart] + actions + xml[endIndex..];
    }
}
