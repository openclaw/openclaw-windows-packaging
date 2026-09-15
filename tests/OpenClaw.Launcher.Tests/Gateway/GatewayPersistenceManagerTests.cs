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

    private GatewayPersistenceManager CreateManager(Action<string>? deleteFile = null) =>
        new(
            _scheduler,
            new GatewayPersistenceOptions(
                UserSid: "S-1-5-21-1",
                PackageFamilyName: "OpenClaw.Gateway_test",
                LauncherPath: LauncherPath,
                StartupFolderPath: StartupFolder,
                WorkingDirectory: StateRoot,
                AliasCommand: "clawctl.exe",
                CommandProcessorPath: @"C:\Windows\System32\cmd.exe",
                DeleteFile: deleteFile));

    private GatewayTaskSnapshot DesiredSnapshot() =>
        GatewayTaskDefinition.CreateSnapshot(
            "S-1-5-21-1",
            @"C:\Windows\System32\cmd.exe",
            LauncherPath);

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
    public async Task UninstallingWithNoArtifactsIsIdempotent()
    {
        GatewayPersistenceRemovalResult result =
            await CreateManager().UninstallAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Null(result.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UninstallingReportsGeneratedArtifactDeletionFailure(bool fallback)
    {
        string fallbackPath = Path.Combine(
            StartupFolder,
            "OpenClaw Gateway OpenClaw.Gateway_test.cmd");
        GatewayPersistenceManager manager = CreateManager(path =>
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
    public async Task AFailedDeletionIsReportedRatherThanClaimedAsSuccess()
    {
        _scheduler.DeleteResult = GatewayTaskOperation.Failure("Access is denied.");

        GatewayPersistenceRemovalResult result =
            await CreateManager().UninstallAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Access is denied.", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLaunchCommandCanChangeWithoutReRegisteringTheTask()
    {
        GatewayPersistenceManager manager = CreateManager();
        await manager.InstallAsync(CancellationToken.None);
        _scheduler.Probe = GatewayTaskProbe.Present(DesiredSnapshot());
        string before = _scheduler.RegisteredXml!;

        await File.WriteAllTextAsync(
            LauncherPath,
            GatewayLauncherScript.Create(StateRoot, "stale.exe"),
            CancellationToken.None);
        GatewayPersistenceInstallResult result =
            await manager.InstallAsync(CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(GatewayPersistenceState.Ready, result.State);
        Assert.Equal(before, _scheduler.RegisteredXml);
        Assert.Contains(
            "clawctl.exe",
            await File.ReadAllTextAsync(
                LauncherPath,
                CancellationToken.None),
            StringComparison.Ordinal);
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
}
