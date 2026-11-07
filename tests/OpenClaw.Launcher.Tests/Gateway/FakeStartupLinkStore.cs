using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// Stores a shortcut's owned fields as a small text file at the shortcut's
/// path, so persistence tests keep real file existence, locking, and deletion
/// without the Windows shell. <see cref="ShellStartupLinkStoreTests"/> proves
/// the shell implementation round-trips the same fields.
/// </summary>
internal sealed class FakeStartupLinkStore : IStartupLinkStore
{
    private const string Header = "fixture-shortcut";

    public int Writes { get; private set; }

    public StartupLink? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string[] lines = File.ReadAllText(path).Split('\n');
        return lines.Length == 4 && lines[0] == Header
            ? new StartupLink(lines[1], lines[2], lines[3])
            : throw new IOException($"'{path}' is not a shortcut.");
    }

    public void Write(string path, StartupLink link)
    {
        Writes++;
        File.WriteAllText(
            path,
            string.Join('\n', Header, link.TargetPath, link.Arguments, link.Description));
    }

    public static void Create(string path, StartupLink link) =>
        new FakeStartupLinkStore().Write(path, link);
}
