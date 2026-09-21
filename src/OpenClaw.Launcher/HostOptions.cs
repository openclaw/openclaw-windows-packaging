namespace OpenClaw.Launcher;

internal sealed record HostOptions(
    string? PackagedApplicationDirectory,
    string? PackagedNodeArchivePath,
    IReadOnlyList<string> OpenClawArguments)
{
    public static HostOptions Parse(IReadOnlyList<string> arguments) =>
        Parse(arguments, AppContext.BaseDirectory);

    internal static HostOptions Parse(
        IReadOnlyList<string> arguments,
        string baseDirectory)
    {
        // openclaw.mjs presence is the sole readiness signal: MSIX enforces
        // package read-only integrity, so no separate hash/marker check is
        // needed here. Null (rather than throwing) defers the "not found"
        // failure to call sites, which can report it with more context.
        string packagedApplicationDirectory = Path.Combine(
            baseDirectory,
            "app");
        string? directApplicationDirectory = File.Exists(Path.Combine(
            packagedApplicationDirectory,
            "openclaw.mjs"))
                ? packagedApplicationDirectory
                : null;
        string? packagedNodeArchivePath = NodeRuntimeInstaller.FindArchivePath(
            Path.Combine(baseDirectory, "runtime"),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

        return new HostOptions(
            directApplicationDirectory,
            packagedNodeArchivePath,
            [.. arguments]);
    }

    /// <summary>
    /// Resolves the packaged application directory, failing with the packaged
    /// entry point's path when it is absent.
    /// </summary>
    /// <remarks>
    /// <see cref="Parse(IReadOnlyList{string}, string)"/> already checked for
    /// <c>openclaw.mjs</c>. Re-checking here makes a never-resolved directory
    /// and one removed since startup fail through the same message.
    /// </remarks>
    public string RequirePackagedApplicationDirectory()
    {
        string entryPoint = Path.Combine(
            PackagedApplicationDirectory ?? Path.Combine(AppContext.BaseDirectory, "app"),
            "openclaw.mjs");
        if (PackagedApplicationDirectory is null || !File.Exists(entryPoint))
        {
            throw new FileNotFoundException(
                "The packaged OpenClaw entry point was not found.",
                entryPoint);
        }

        return PackagedApplicationDirectory;
    }

    public string RequirePackagedNodeArchivePath()
    {
        if (PackagedNodeArchivePath is null || !File.Exists(PackagedNodeArchivePath))
        {
            throw new FileNotFoundException(
                "The packaged Node.js runtime archive was not found.",
                PackagedNodeArchivePath ?? Path.Combine(AppContext.BaseDirectory, "runtime"));
        }

        return PackagedNodeArchivePath;
    }
}
