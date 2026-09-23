using System.Diagnostics;
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

    private string LauncherPath => Path.Combine(StateRoot, "gateway-launcher.cmd");

    private string ActivationScriptPath => Path.ChangeExtension(LauncherPath, ".ps1");

    private GatewayPersistenceManager CreateManager(
        Func<string, string?>? resolveUserSid = null,
        Action<string>? deleteFile = null) =>
        new(
            _scheduler,
            new GatewayPersistenceOptions(
                UserSid: "S-1-5-21-1",
                PackageFamilyName: "OpenClaw.Gateway_test",
                LauncherPath: LauncherPath,
                StartupFolderPath: StartupFolder,
                WorkingDirectory: StateRoot,
                CommandProcessorPath: @"C:\Windows\System32\cmd.exe",
                DeleteFile: deleteFile),
            resolveUserSid: resolveUserSid);

    private GatewayTaskSnapshot DesiredSnapshot() =>
        GatewayTaskDefinition.CreateSnapshot(
            "S-1-5-21-1",
            @"C:\Windows\System32\cmd.exe",
            LauncherPath);

    [Theory]
    [InlineData(@"C:\outside\gateway-launcher.cmd")]
    [InlineData(@"state\child\gateway-launcher.cmd")]
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
            CommandProcessorPath: @"C:\Windows\System32\cmd.exe");

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

        Assert.Contains("chcp 65001", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenClaw.Gateway_test!Control", activation, StringComparison.Ordinal);
        Assert.Contains("ActivateApplication", activation, StringComparison.Ordinal);
        Assert.Contains(
            "gateway-service start --recovery",
            activation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsApps", launcher + activation, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("Get-AppxPackage -Name 'OpenClawFoundation.OpenClawGateway'", activation, StringComparison.Ordinal);
        Assert.Contains("does not declare application '$applicationId'", activation, StringComparison.Ordinal);
        Assert.Contains("does not target openclaw.exe", activation, StringComparison.Ordinal);
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
    public async Task GeneratedCommandFilesUseUtf8WithoutABom()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();

        await manager.InstallAsync(CancellationToken.None);

        Assert.DoesNotContain((byte)0, await File.ReadAllBytesAsync(LauncherPath));
        Assert.DoesNotContain((byte)0, await File.ReadAllBytesAsync(manager.FallbackPath));
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF },
            (await File.ReadAllBytesAsync(LauncherPath))[..3]);
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
            "OpenClaw.Gateway_test!Control",
            await File.ReadAllTextAsync(
                ActivationScriptPath,
                CancellationToken.None),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackageActivationSourceCompilesWithWindowsPowerShell()
    {
        string script = GatewayLauncherScript.CreateActivationScript(
            "OpenClaw.Gateway_test");
        int sourceIndex = script.IndexOf(
            "$source = @'",
            StringComparison.Ordinal);
        int addTypeIndex = script.IndexOf(
            "Add-Type -TypeDefinition $source -Language CSharp",
            sourceIndex,
            StringComparison.Ordinal);
        Assert.True(sourceIndex > 0);
        Assert.True(addTypeIndex > sourceIndex);
        int addTypeEnd = script.IndexOf("\r\n", addTypeIndex, StringComparison.Ordinal);
        Assert.True(addTypeEnd > addTypeIndex);
        string compileOnlyPath = Path.Combine(_root, "compile-activation.ps1");
        File.WriteAllText(
            compileOnlyPath,
            string.Concat(
                "$ErrorActionPreference = 'Stop'\r\n",
                script.AsSpan(sourceIndex, addTypeEnd - sourceIndex),
                "\r\nexit 0\r\n"));

        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                compileOnlyPath
            }
        })!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"Windows PowerShell exited {process.ExitCode}.{Environment.NewLine}" +
            $"{output}{Environment.NewLine}{error}");
    }

    [Fact]
    public async Task TheLauncherEntersAControlledWorkingDirectory()
    {
        // At logon the task's working directory is the system directory.
        await CreateManager().InstallAsync(CancellationToken.None);

        Assert.Contains(
            $"cd /d \"{StateRoot}\"",
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
