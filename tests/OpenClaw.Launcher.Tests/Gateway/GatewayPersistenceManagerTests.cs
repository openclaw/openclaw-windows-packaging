using System.Diagnostics;
using System.Text;
using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayPersistenceManagerTests : IDisposable
{
    private const string PackageFamilyName = "OpenClaw.Gateway_test";

    private readonly string _root = TestDirectory.Create();
    private readonly FakeGatewayTaskScheduler _scheduler = new();
    private readonly FakeStartupLinkStore _links = new();

    public GatewayPersistenceManagerTests()
    {
        Directory.CreateDirectory(StateRoot);
        Directory.CreateDirectory(StartupFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(HelperPath)!);
        File.WriteAllText(HelperPath, "fixture alias");
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

    private string StateRoot => Path.Combine(_root, "state");

    private string StartupFolder => Path.Combine(_root, "startup");

    private string HelperPath => Path.Combine(
        _root,
        "WindowsApps",
        PackageFamilyName,
        GatewayPersistenceManager.HelperExecutable);

    private string LegacyLauncherPath => Path.Combine(StateRoot, "gateway-launcher.js");

    private string LegacyActivationScriptPath => Path.ChangeExtension(LegacyLauncherPath, ".ps1");

    private string LegacyCmdLauncherPath => Path.ChangeExtension(LegacyLauncherPath, ".cmd");

    private static string LegacyScriptHostLauncher =>
        GatewayLauncherScript.ScriptHostMarker + "\r\nWScript.Quit(0);\r\n";

    private static string LegacyScriptHostFallback(string launcherPath) =>
        $"<job><!-- {GatewayLauncherScript.ScriptHostMarker} --><script language=\"JScript\" src=\"{launcherPath}\" /></job>";

    private GatewayPersistenceManager CreateManager(
        Func<string, string?>? resolveUserSid = null,
        Action<string>? deleteFile = null,
        Action<string>? log = null,
        IStartupLinkStore? links = null,
        string? startupFolder = null) =>
        new(
            _scheduler,
            new GatewayPersistenceOptions(
                UserSid: "S-1-5-21-1",
                PackageFamilyName: PackageFamilyName,
                HelperPath: HelperPath,
                LegacyLauncherPath: LegacyLauncherPath,
                StartupFolderPath: startupFolder ?? StartupFolder,
                StateDirectory: StateRoot,
                StartupLinks: links ?? _links,
                DeleteFile: deleteFile),
            log,
            resolveUserSid: resolveUserSid);

    private GatewayTaskSnapshot DesiredSnapshot() =>
        GatewayTaskDefinition.CreateSnapshot("S-1-5-21-1", HelperPath);

    private GatewayTaskSnapshot ScriptHostGenerationTask() =>
        DesiredSnapshot() with
        {
            Command = @"C:\Windows\System32\wscript.exe",
            Arguments = $"//B //Nologo \"{LegacyLauncherPath}\""
        };

    private GatewayTaskSnapshot ConsoleGenerationTask() =>
        DesiredSnapshot() with
        {
            Command = @"C:\Windows\System32\cmd.exe",
            Arguments = $"/d /c \"\"{LegacyCmdLauncherPath}\"\""
        };

    private StartupLink OwnedLink(string targetPath) =>
        new(targetPath, string.Empty, GatewayPersistenceManager.StartupLinkDescription);

    [Theory]
    [InlineData(@"C:\outside\gateway-launcher.js")]
    [InlineData(@"state\child\gateway-launcher.js")]
    public void RejectsALegacyLauncherPathOutsideTheStateDirectory(string launcherPath)
    {
        GatewayPersistenceOptions options = new(
            UserSid: "S-1-5-21-1",
            PackageFamilyName: PackageFamilyName,
            HelperPath: HelperPath,
            LegacyLauncherPath: Path.IsPathRooted(launcherPath)
                ? launcherPath
                : Path.Combine(_root, launcherPath),
            StartupFolderPath: StartupFolder,
            StateDirectory: StateRoot,
            StartupLinks: _links);

        Assert.Throws<ArgumentException>(() =>
            new GatewayPersistenceManager(_scheduler, options));
    }

    [Fact]
    public async Task InstallingRegistersATaskThatRunsTheHelperAndWritesNoScript()
    {
        GatewayPersistenceManager manager = CreateManager();

        GatewayPersistenceInstallResult result =
            await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.Equal(GatewayPersistenceLane.TaskScheduler, result.Lane);
        Assert.True(result.Changed);
        Assert.Contains($"register:{manager.TaskName}", _scheduler.Calls);
        Assert.True(GatewayTaskDefinition.TryParse(
            _scheduler.RegisteredXml!,
            out GatewayTaskSnapshot? registered,
            out _));
        Assert.Equal($"\"{HelperPath}\"", registered!.Command);
        Assert.Equal(string.Empty, registered.Arguments);
        Assert.DoesNotContain("wscript", _scheduler.RegisteredXml, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(StateRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(StartupFolder));
    }

    [Fact]
    public async Task SuccessfulRegistrationWithTriggerDriftDoesNotClaimRecoveryIsReady()
    {
        _scheduler.RegistrationProbe = GatewayTaskProbe.Present(DesiredSnapshot() with
        {
            LogonTriggerUserId = "S-1-5-21-2"
        });
        GatewayPersistenceManager manager = CreateManager();
        FakeStartupLinkStore.Create(manager.FallbackPath, OwnedLink(HelperPath));

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
    public async Task AMatchingRegistrationIsNotRewritten()
    {
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot());
        GatewayPersistenceManager manager = CreateManager();

        GatewayPersistenceInstallResult result =
            await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.False(result.Changed);
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
    public async Task AFailedRegistrationFallsBackToAStartupShortcutToTheHelperAndSaysSo()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();

        GatewayPersistenceInstallResult result =
            await manager.InstallAsync(CancellationToken.None);
        GatewayPersistenceStatus status =
            await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.Equal(GatewayPersistenceLane.StartupFolderFallback, result.Lane);
        Assert.Contains("Access is denied.", result.Detail, StringComparison.Ordinal);
        Assert.Contains("Startup-folder shortcut", result.Detail, StringComparison.Ordinal);
        Assert.Equal(".lnk", Path.GetExtension(manager.FallbackPath));
        Assert.Equal(OwnedLink(HelperPath), _links.Read(manager.FallbackPath));
        Assert.Equal(GatewayPersistenceState.Ready, status.State);
        Assert.Equal(GatewayPersistenceLane.StartupFolderFallback, status.Lane);
        Assert.Contains("Startup-folder shortcut", status.Message, StringComparison.Ordinal);
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
    public async Task ReinstallingTheFallbackDoesNotRewriteAMatchingShortcut()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);

        GatewayPersistenceInstallResult again = await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, again.State);
        Assert.False(again.Changed);
        Assert.Equal(1, _links.Writes);
    }

    [Fact]
    public async Task FallbackStatusRejectsAnOwnedShortcutWithARedirectedTarget()
    {
        GatewayPersistenceManager manager = CreateManager();
        FakeStartupLinkStore.Create(
            manager.FallbackPath,
            OwnedLink(Path.Combine(_root, "redirected.exe")));

        GatewayPersistenceStatus status =
            await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.NotInstalled, status.State);
    }

    [Fact]
    public async Task AShortcutSomeoneElseMadeIsNeitherReplacedNorRemoved()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        StartupLink theirs = new(@"C:\Tools\other.exe", "--run", "Someone else's shortcut");
        FakeStartupLinkStore.Create(manager.FallbackPath, theirs);

        GatewayPersistenceInstallResult install = await manager.InstallAsync(CancellationToken.None);
        GatewayPersistenceRemovalResult removal = await manager.UninstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, install.State);
        Assert.Contains("was not created by OpenClaw", install.Detail, StringComparison.Ordinal);
        Assert.True(removal.Succeeded);
        Assert.Equal(theirs, _links.Read(manager.FallbackPath));
    }

    [Fact]
    public async Task AMissingStartupFolderIsReportedRatherThanCreated()
    {
        // A folder created from inside the package would be redirected into
        // its private AppData, where Explorer never looks at sign-in.
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        string missing = Path.Combine(_root, "missing-startup");
        GatewayPersistenceManager manager = CreateManager(startupFolder: missing);

        GatewayPersistenceInstallResult result = await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, result.State);
        Assert.Contains(missing, result.Detail, StringComparison.Ordinal);
        Assert.Equal(GatewayPersistenceManager.RepairCommand, result.Remediation);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task ASucceedingRegistrationRemovesAPreviousShortcut()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);

        _scheduler.RegisterResult = GatewayTaskOperation.Success;
        GatewayPersistenceInstallResult result = await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceLane.TaskScheduler, result.Lane);
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
        Assert.All(
            _scheduler.Calls,
            call => Assert.StartsWith("query:", call, StringComparison.Ordinal));
        Assert.Equal(0, _links.Writes);
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
    [InlineData("script-host")]
    public async Task StatusReportsDriftWithTheCommandThatRepairsIt(string kind)
    {
        GatewayTaskSnapshot desired = DesiredSnapshot();
        _scheduler.Probe = GatewayTaskProbe.Present(kind switch
        {
            "disabled" => desired with { Enabled = false },
            "battery" => desired with { DisallowStartIfOnBatteries = true },
            "elevated" => desired with { RunLevel = "HighestAvailable" },
            "other-user" => desired with { UserId = "S-1-5-21-999" },
            "time-limit" => desired with { ExecutionTimeLimit = "PT72H" },
            "script-host" => ScriptHostGenerationTask(),
            _ => desired with { Command = @"C:\Windows\System32\notepad.exe" }
        });

        GatewayPersistenceStatus status =
            await CreateManager().GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, status.State);
        Assert.Equal(GatewayPersistenceManager.RepairCommand, status.Remediation);
        Assert.NotNull(status.Detail);
    }

    [Fact]
    public async Task AnInstalledInstallationIsReportedAsReady()
    {
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);

        GatewayPersistenceStatus status =
            await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, status.State);
        Assert.Equal(GatewayPersistenceLane.TaskScheduler, status.Lane);
        Assert.Null(status.Detail);
    }

    [Fact]
    public async Task AMissingHelperBlocksInstallAndIsReportedForBothLanes()
    {
        // A task or shortcut naming a missing alias would fail silently at
        // every sign-in, which is the failure this guards against.
        GatewayPersistenceManager manager = CreateManager();
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot());
        File.Delete(HelperPath);

        GatewayPersistenceStatus taskStatus = await manager.GetStatusAsync(CancellationToken.None);
        _scheduler.Probe = GatewayTaskProbe.Missing;
        FakeStartupLinkStore.Create(manager.FallbackPath, OwnedLink(HelperPath));
        GatewayPersistenceStatus fallbackStatus = await manager.GetStatusAsync(CancellationToken.None);
        int writesBefore = _links.Writes;
        GatewayPersistenceInstallResult install = await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, taskStatus.State);
        Assert.Equal(GatewayPersistenceLane.TaskScheduler, taskStatus.Lane);
        Assert.Contains(HelperPath, taskStatus.Detail, StringComparison.Ordinal);
        Assert.Equal(GatewayPersistenceState.ActionRequired, fallbackStatus.State);
        Assert.Equal(GatewayPersistenceLane.StartupFolderFallback, fallbackStatus.Lane);
        Assert.Equal(GatewayPersistenceState.ActionRequired, install.State);
        Assert.Equal(GatewayPersistenceManager.RepairCommand, install.Remediation);
        Assert.DoesNotContain(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
        Assert.Equal(writesBefore, _links.Writes);
    }

    [Fact]
    public async Task UninstallingRemovesTheTaskAndTheShortcut()
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);

        GatewayPersistenceRemovalResult result =
            await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.False(File.Exists(manager.FallbackPath));
        Assert.Contains($"delete:{manager.TaskName}", _scheduler.Calls);
        Assert.Equal(
            GatewayPersistenceState.NotInstalled,
            (await manager.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task UninstallingLeavesFilesThisInstallationDidNotGenerate()
    {
        GatewayPersistenceManager manager = CreateManager();
        string[] userFiles =
        [
            Path.ChangeExtension(manager.FallbackPath, ".cmd"),
            Path.ChangeExtension(manager.FallbackPath, ".wsf"),
            LegacyLauncherPath,
        ];
        foreach (string path in userFiles)
        {
            await File.WriteAllTextAsync(path, "someone else's script", CancellationToken.None);
        }

        GatewayPersistenceRemovalResult result = await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.All(userFiles, path => Assert.True(File.Exists(path), path));
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
    [InlineData("shortcut")]
    [InlineData("legacy-fallback")]
    [InlineData("legacy-launcher")]
    public async Task UninstallingReportsAnOwnedFileThatCannotBeDeleted(string kind)
    {
        string fallbackPath = CreateManager().FallbackPath;
        string target = kind switch
        {
            "shortcut" => fallbackPath,
            "legacy-fallback" => Path.ChangeExtension(fallbackPath, ".wsf"),
            _ => LegacyLauncherPath
        };
        GatewayPersistenceManager manager = CreateManager(deleteFile: path =>
        {
            if (string.Equals(path, target, StringComparison.Ordinal))
            {
                throw new IOException("The generated file is locked.");
            }

            File.Delete(path);
        });
        if (kind == "shortcut")
        {
            FakeStartupLinkStore.Create(target, OwnedLink(HelperPath));
        }
        else
        {
            await File.WriteAllTextAsync(target, LegacyScriptHostLauncher);
        }

        GatewayPersistenceRemovalResult result =
            await manager.UninstallAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("locked", result.Detail, StringComparison.Ordinal);
        Assert.Contains(target, result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task TheWindowsScriptHostGenerationIsReportedThenMigratedToTheHelper()
    {
        // The shipped generation: a wscript logon task, its launcher and
        // activation script, and a Startup WSF that a refused registration left.
        GatewayPersistenceManager manager = CreateManager();
        string legacyFallback = Path.ChangeExtension(manager.FallbackPath, ".wsf");
        await File.WriteAllTextAsync(LegacyLauncherPath, LegacyScriptHostLauncher, Encoding.Unicode);
        await File.WriteAllTextAsync(
            LegacyActivationScriptPath,
            GatewayLauncherScript.CreateActivationScript(PackageFamilyName));
        await File.WriteAllTextAsync(legacyFallback, LegacyScriptHostFallback(LegacyLauncherPath));
        _scheduler.Probe = GatewayTaskProbe.Present(ScriptHostGenerationTask());

        GatewayPersistenceStatus before = await manager.GetStatusAsync(CancellationToken.None);
        GatewayPersistenceInstallResult install = await manager.InstallAsync(CancellationToken.None);
        GatewayPersistenceStatus after = await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, before.State);
        Assert.Contains("task runs a different command", before.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GatewayPersistenceState.Ready, install.State);
        Assert.Equal(GatewayPersistenceLane.TaskScheduler, install.Lane);
        Assert.Equal(DesiredSnapshot(), _scheduler.Probe.Snapshot);
        Assert.False(File.Exists(LegacyLauncherPath));
        Assert.False(File.Exists(LegacyActivationScriptPath));
        Assert.False(File.Exists(legacyFallback));
        Assert.Equal(GatewayPersistenceState.Ready, after.State);
    }

    [Fact]
    public async Task ARefusedLegacyTaskMigrationKeepsItsReferencedFiles()
    {
        GatewayPersistenceManager manager = CreateManager();
        string legacyFallback = Path.ChangeExtension(manager.FallbackPath, ".wsf");
        await File.WriteAllTextAsync(LegacyLauncherPath, LegacyScriptHostLauncher, Encoding.Unicode);
        await File.WriteAllTextAsync(legacyFallback, LegacyScriptHostFallback(LegacyLauncherPath));
        _scheduler.Probe = GatewayTaskProbe.Present(ScriptHostGenerationTask());
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");

        GatewayPersistenceInstallResult result = await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, result.State);
        Assert.Equal(GatewayPersistenceLane.StartupFolderFallback, result.Lane);
        Assert.Equal(GatewayPersistenceManager.RepairCommand, result.Remediation);
        Assert.Contains("existing logon task", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LegacyScriptHostLauncher, await File.ReadAllTextAsync(LegacyLauncherPath));
        Assert.True(File.Exists(legacyFallback));
        Assert.Equal(OwnedLink(HelperPath), _links.Read(manager.FallbackPath));
        Assert.Equal(GatewayPersistenceState.ActionRequired,
            (await manager.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task ReinstallMigratesOnlyGeneratedConsoleGenerationFiles()
    {
        GatewayPersistenceManager manager = CreateManager();
        string legacyFallback = Path.ChangeExtension(manager.FallbackPath, ".cmd");
        await File.WriteAllTextAsync(LegacyCmdLauncherPath, GatewayLauncherScript.Marker);
        await File.WriteAllTextAsync(legacyFallback, GatewayLauncherScript.Marker);
        await File.WriteAllTextAsync(
            LegacyActivationScriptPath,
            GatewayLauncherScript.CreateLegacyActivationScript(PackageFamilyName, "gateway-service start"));
        _scheduler.Probe = GatewayTaskProbe.Present(ConsoleGenerationTask());

        Assert.Equal(GatewayPersistenceState.ActionRequired,
            (await manager.GetStatusAsync(CancellationToken.None)).State);
        GatewayPersistenceInstallResult result = await manager.InstallAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.False(File.Exists(LegacyCmdLauncherPath));
        Assert.False(File.Exists(legacyFallback));
        Assert.False(File.Exists(LegacyActivationScriptPath));
        Assert.True(GatewayTaskDefinition.TryParse(_scheduler.RegisteredXml!, out GatewayTaskSnapshot? registered, out _));
        Assert.Equal(DesiredSnapshot(), registered);

        await File.WriteAllTextAsync(legacyFallback, "user-owned file");
        await manager.UninstallAsync(CancellationToken.None);
        Assert.Equal("user-owned file", await File.ReadAllTextAsync(legacyFallback));
    }

    [Fact]
    public async Task AnUnownedLegacyFallbackCmdDoesNotMakeTheInstalledFallbackNeedAttention()
    {
        // Registration is refused, so the Startup-folder shortcut is the lane. A
        // CMD with the legacy name that this installation did not generate is
        // the user's file: install preserves it, so status must not demand a
        // reinstall that cannot remove it.
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        string unowned = Path.ChangeExtension(manager.FallbackPath, ".cmd");
        const string userContent = "@echo off\r\nrem a user's own startup helper\r\n";
        await File.WriteAllTextAsync(unowned, userContent);

        GatewayPersistenceInstallResult install = await manager.InstallAsync(CancellationToken.None);
        GatewayPersistenceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.Ready, install.State);
        Assert.Equal(GatewayPersistenceState.Ready, status.State);
        Assert.Equal(GatewayPersistenceLane.StartupFolderFallback, status.Lane);
        Assert.Equal(userContent, await File.ReadAllTextAsync(unowned));
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".wsf")]
    public async Task AGeneratedLegacyFallbackIsReportedThenRemovedByAFallbackInstall(string extension)
    {
        _scheduler.RegisterResult = GatewayTaskOperation.Failure("Access is denied.");
        GatewayPersistenceManager manager = CreateManager();
        string legacy = Path.ChangeExtension(manager.FallbackPath, extension);
        await File.WriteAllTextAsync(
            legacy,
            extension == ".cmd" ? GatewayLauncherScript.Marker : LegacyScriptHostFallback(LegacyLauncherPath));

        GatewayPersistenceStatus before = await manager.GetStatusAsync(CancellationToken.None);
        GatewayPersistenceInstallResult install = await manager.InstallAsync(CancellationToken.None);
        GatewayPersistenceStatus after = await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, before.State);
        Assert.Equal(GatewayPersistenceLane.StartupFolderFallback, before.Lane);
        Assert.Contains(legacy, before.Detail, StringComparison.Ordinal);
        Assert.Equal(GatewayPersistenceManager.RepairCommand, before.Remediation);
        Assert.Equal(GatewayPersistenceState.Ready, install.State);
        Assert.False(File.Exists(legacy));
        Assert.Equal(GatewayPersistenceState.Ready, after.State);
    }

    [Fact]
    public async Task AFailedLegacyCleanupIsReportedByInstallAndStatusAlike()
    {
        GatewayPersistenceManager manager = CreateManager(deleteFile: path =>
        {
            if (Path.GetExtension(path) == ".cmd")
            {
                throw new IOException("legacy file is locked");
            }
            File.Delete(path);
        });
        await File.WriteAllTextAsync(LegacyCmdLauncherPath, GatewayLauncherScript.Marker);

        GatewayPersistenceInstallResult result = await manager.InstallAsync(CancellationToken.None);
        GatewayPersistenceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayPersistenceState.ActionRequired, result.State);
        Assert.Contains("legacy file is locked", result.Detail, StringComparison.Ordinal);
        Assert.Equal(GatewayPersistenceManager.RepairCommand, result.Remediation);
        Assert.True(File.Exists(LegacyCmdLauncherPath));
        Assert.Equal(GatewayPersistenceState.ActionRequired, status.State);
        Assert.Equal(GatewayPersistenceLane.TaskScheduler, status.Lane);
        Assert.Contains(LegacyCmdLauncherPath, status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyActivationUpgradeDoesNotOverwriteModifiedContent()
    {
        string modified = GatewayLauncherScript.CreateActivationScript(PackageFamilyName)
            .Replace(
                GatewayLauncherScript.ControlArguments,
                "gateway-service start",
                StringComparison.Ordinal) +
            "# user edit";
        await File.WriteAllTextAsync(LegacyActivationScriptPath, modified, CancellationToken.None);

        bool legacyInvocation =
            GatewayLauncherScript.UpgradeLegacyActivationScript(
                LegacyActivationScriptPath,
                PackageFamilyName,
                _ => { });

        Assert.False(legacyInvocation);
        Assert.Equal(
            modified,
            await File.ReadAllTextAsync(LegacyActivationScriptPath, CancellationToken.None));
    }

    [Theory]
    [InlineData(
        "OpenClawFoundation.OpenClawGateway_rfcbke2p71se2",
        "OpenClawFoundation.OpenClawGateway")]
    [InlineData(
        "OpenClawFoundation.OpenClawGateway-foo_rfcbke2p71se2",
        "OpenClawFoundation.OpenClawGateway-foo")]
    public void TheUpgradedActivationScriptLooksUpTheIdentityThatGeneratedIt(
        string packageFamilyName,
        string packageName)
    {
        // A side-by-side identity's retained recovery must find its own package;
        // looking up the base name would report it as not registered.
        string activation =
            GatewayLauncherScript.CreateActivationScript(packageFamilyName);

        Assert.Contains(
            $"$package = Get-AppxPackage -Name '{packageName}' | ",
            activation,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other.exe", "clawctl.exe", "does not target openclaw.exe")]
    [InlineData("openclaw.exe", "openclaw.exe", "unexpected control alias")]
    [InlineData("openclaw.exe", "../clawctl.exe", "unexpected control alias")]
    public void TheUpgradedActivationScriptRejectsInvalidPackageTargetsBeforeLaunching(
        string executable, string alias, string expectedError)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(Path.Combine(_root, "AppxManifest.xml"), $"""
            <Package><Applications><Application Id="Control" Executable="{executable}">
            <Extensions><Extension Category="windows.appExecutionAlias"><AppExecutionAlias>
            <ExecutionAlias Alias="{alias}" /></AppExecutionAlias></Extension></Extensions>
            </Application></Applications></Package>
            """);
        string scriptPath = Path.Combine(_root, "invalid-target.ps1");
        string fixtureRoot = _root.Replace("'", "''", StringComparison.Ordinal);
        File.WriteAllText(scriptPath,
            $"function Get-AppxPackage {{ [pscustomobject]@{{ PackageFamilyName = '{PackageFamilyName}'; Status = 'Ok'; InstallLocation = '{fixtureRoot}' }} }}\n" +
            GatewayLauncherScript.CreateActivationScript(PackageFamilyName),
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
