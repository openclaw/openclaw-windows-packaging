using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// Drives the shell's own shortcut object against fixture-owned files, which
/// is what Explorer reads when it starts Startup-folder items.
/// </summary>
public sealed class ShellStartupLinkStoreTests : IDisposable
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

    [Fact]
    public void AShortcutRoundTripsTheFieldsRecoveryOwns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string target = CreateHelper(Path.Combine(_root, "user name 日本語"));
        string path = Path.Combine(_root, "OpenClaw Gateway OpenClaw.Gateway_test.lnk");
        var store = new ShellStartupLinkStore();
        StartupLink link = new(target, string.Empty, GatewayPersistenceManager.StartupLinkDescription);

        store.Write(path, link);

        Assert.Equal(link, store.Read(path));
    }

    [Fact]
    public void AMissingShortcutReadsAsAbsent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null(new ShellStartupLinkStore().Read(Path.Combine(_root, "missing.lnk")));
    }

    [Fact]
    public void AFileThatIsNotAShortcutIsUnreadableRatherThanSomeoneElsesLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(_root, "not-a-shortcut.lnk");
        File.WriteAllText(path, "@echo this is not a shortcut");

        Assert.Throws<IOException>(() => new ShellStartupLinkStore().Read(path));
    }

    [Fact]
    public async Task ARefusedRegistrationWritesAShellShortcutThatStartsTheHelper()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string helper = CreateHelper(Path.Combine(_root, "WindowsApps"));
        string startup = Path.Combine(_root, "Startup");
        string state = Path.Combine(_root, "state");
        Directory.CreateDirectory(startup);
        Directory.CreateDirectory(state);
        var store = new ShellStartupLinkStore();
        var manager = new GatewayPersistenceManager(
            new FakeGatewayTaskScheduler
            {
                RegisterResult = GatewayTaskOperation.Failure("Access is denied.")
            },
            new GatewayPersistenceOptions(
                UserSid: "S-1-5-21-1",
                PackageFamilyName: "OpenClaw.Gateway_test",
                HelperPath: helper,
                LegacyLauncherPath: Path.Combine(state, "gateway-launcher.js"),
                StartupFolderPath: startup,
                StateDirectory: state,
                StartupLinks: store));

        GatewayPersistenceInstallResult install = await manager.InstallAsync(CancellationToken.None);
        StartupLink? written = store.Read(manager.FallbackPath);
        GatewayPersistenceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        // Removal is proven against fixture shortcuts: a shortcut the shell has
        // just written can still be open in an antivirus scan for a moment.
        Assert.Equal(GatewayPersistenceState.Ready, install.State);
        Assert.Equal(helper, written?.TargetPath);
        Assert.Equal(string.Empty, written?.Arguments);
        Assert.Equal(GatewayPersistenceState.Ready, status.State);
        Assert.Equal(GatewayPersistenceLane.StartupFolderFallback, status.Lane);
    }

    private static string CreateHelper(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, GatewayPersistenceManager.HelperExecutable);
        File.WriteAllText(path, "fixture");
        return path;
    }
}
