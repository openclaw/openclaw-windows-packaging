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
}
