namespace OpenClaw.Launcher.Gateway;

/// <summary>The fields of a Startup-folder shortcut that recovery owns.</summary>
internal sealed record StartupLink(
    string TargetPath,
    string Arguments,
    string Description);

/// <summary>
/// Reads and writes shell shortcuts. The seam keeps the persistence decisions
/// testable on any machine; the Windows implementation is the shell's own
/// shortcut object, so Explorer reads exactly what was written.
/// </summary>
internal interface IStartupLinkStore
{
    /// <summary>
    /// Returns <see langword="null"/> when no file exists at <paramref name="path"/>.
    /// Throws <see cref="IOException"/> when a file exists but cannot be read
    /// as a shortcut, so an unreadable file is never taken as anyone's link.
    /// </summary>
    StartupLink? Read(string path);

    void Write(string path, StartupLink link);
}
