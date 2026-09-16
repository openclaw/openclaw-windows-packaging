using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class InstallationStateCleanerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ClearRemovesKnownCorruptAndUnknownInstallationFiles()
    {
        string stateRoot = Path.Combine(_root, "state");
        string dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(stateRoot, "Logs"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "NodeJS"));
        File.WriteAllText(Path.Combine(stateRoot, "session.json"), "{not-json");
        File.WriteAllText(Path.Combine(stateRoot, "unknown.bin"), "leftover");
        File.WriteAllText(Path.Combine(stateRoot, "Logs", "openclaw.log"), "log");
        File.WriteAllText(Path.Combine(dataRoot, "NodeJS", "node.exe"), "runtime");

        var cleaner = new InstallationStateCleaner(
            HostPaths.ForRoot(stateRoot, "OpenClaw.Gateway_test"),
            dataRoot);

        cleaner.Clear();

        Assert.Empty(Directory.EnumerateFileSystemEntries(stateRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dataRoot));
    }

    [Fact]
    public void ClearRejectsAReparsePointRoot()
    {
        string stateRoot = Path.Combine(_root, "state");
        string target = Path.Combine(_root, "external");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "must-survive.txt"), "outside");
        Directory.CreateSymbolicLink(stateRoot, target);

        Assert.Throws<SessionException>(() => new InstallationStateCleaner(
            HostPaths.ForRoot(stateRoot, "OpenClaw.Gateway_test"),
            Path.Combine(_root, "data")));
        Assert.True(File.Exists(Path.Combine(target, "must-survive.txt")));
    }

    [Fact]
    public void ClearRejectsAReplacedRootWithoutTouchingTheReplacement()
    {
        string stateRoot = Path.Combine(_root, "state");
        string dataRoot = Path.Combine(_root, "data");
        string movedStateRoot = Path.Combine(_root, "state-before-replacement");
        string externalRoot = Path.Combine(_root, "external");
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(externalRoot);
        File.WriteAllText(Path.Combine(stateRoot, "owned.txt"), "owned");
        File.WriteAllText(Path.Combine(dataRoot, "owned-runtime.txt"), "owned");
        File.WriteAllText(Path.Combine(externalRoot, "must-survive.txt"), "outside");
        bool replaced = false;

        var cleaner = new InstallationStateCleaner(
            [stateRoot, dataRoot],
            beforeTraversal: root =>
            {
                if (!replaced && string.Equals(root, stateRoot, StringComparison.OrdinalIgnoreCase))
                {
                    replaced = true;
                    Directory.Move(stateRoot, movedStateRoot);
                    Directory.CreateSymbolicLink(stateRoot, externalRoot);
                }
            });

        Assert.Throws<SessionException>(() => cleaner.Clear());

        Assert.True(replaced);
        Assert.True(File.Exists(Path.Combine(externalRoot, "must-survive.txt")));
        Assert.True(File.Exists(Path.Combine(dataRoot, "owned-runtime.txt")));
        Assert.True(File.Exists(Path.Combine(movedStateRoot, "owned.txt")));
    }

    [Fact]
    public void ClearRefusesADescendantReplacedAfterEnumeration()
    {
        string stateRoot = Path.Combine(_root, "state");
        string dataRoot = Path.Combine(_root, "data");
        string externalRoot = Path.Combine(_root, "external");
        string movedOwnedDirectory = Path.Combine(_root, "owned-before-replacement");
        string ownedDirectory = Path.Combine(stateRoot, "owned");
        string ownedFile = Path.Combine(stateRoot, "delete-me.txt");
        Directory.CreateDirectory(ownedDirectory);
        Directory.CreateDirectory(externalRoot);
        File.WriteAllText(Path.Combine(ownedDirectory, "owned.txt"), "owned");
        File.WriteAllText(ownedFile, "owned");
        File.WriteAllText(Path.Combine(externalRoot, "must-survive.txt"), "outside");
        bool replaced = false;

        var cleaner = new InstallationStateCleaner(
            [stateRoot, dataRoot],
            beforeDeleteEntry: entry =>
            {
                if (!replaced && string.Equals(entry, ownedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    replaced = true;
                    Directory.Move(ownedDirectory, movedOwnedDirectory);
                    Directory.CreateSymbolicLink(ownedDirectory, externalRoot);
                }
            });

        cleaner.Clear();

        Assert.True(replaced);
        Assert.True(File.Exists(Path.Combine(externalRoot, "must-survive.txt")));
        Assert.True(File.Exists(Path.Combine(movedOwnedDirectory, "owned.txt")));
        Assert.False(File.Exists(ownedFile));
        Assert.False(Directory.Exists(ownedDirectory));
    }

    [Fact]
    public void ClearRejectsAReparsePointAncestor()
    {
        string external = Path.Combine(_root, "external");
        string redirectedParent = Path.Combine(_root, "redirected");
        string stateRoot = Path.Combine(redirectedParent, "state");
        Directory.CreateDirectory(external);
        Directory.CreateSymbolicLink(redirectedParent, external);
        Directory.CreateDirectory(stateRoot);
        File.WriteAllText(Path.Combine(stateRoot, "must-survive.txt"), "outside");

        Assert.Throws<SessionException>(() => new InstallationStateCleaner(
            HostPaths.ForRoot(stateRoot, "OpenClaw.Gateway_test"),
            Path.Combine(_root, "data")));
        Assert.True(File.Exists(Path.Combine(stateRoot, "must-survive.txt")));
    }

    [Fact]
    public void ClearUnlinksAnOwnedReparsePointWithoutFollowingIt()
    {
        string stateRoot = Path.Combine(_root, "state");
        string externalRoot = Path.Combine(_root, "external");
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(externalRoot);
        File.WriteAllText(Path.Combine(externalRoot, "must-survive.txt"), "outside");
        string link = Path.Combine(stateRoot, "link");
        Directory.CreateSymbolicLink(link, externalRoot);

        new InstallationStateCleaner(
            HostPaths.ForRoot(stateRoot, "OpenClaw.Gateway_test"),
            Path.Combine(_root, "data")).Clear();

        Assert.False(File.Exists(link));
        Assert.True(File.Exists(Path.Combine(externalRoot, "must-survive.txt")));
    }
}
