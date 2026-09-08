namespace OpenClaw.Launcher;

public sealed record HostOptions(
    string? PackagedApplicationDirectory,
    IReadOnlyList<string> OpenClawArguments)
{
    public static HostOptions Parse(IReadOnlyList<string> arguments) =>
        Parse(arguments, AppContext.BaseDirectory);

    internal static HostOptions Parse(
        IReadOnlyList<string> arguments,
        string baseDirectory)
    {
        string packagedApplicationDirectory = Path.Combine(
            baseDirectory,
            "app");
        string? directApplicationDirectory = File.Exists(Path.Combine(
            packagedApplicationDirectory,
            "openclaw.mjs"))
                ? packagedApplicationDirectory
                : null;

        return new HostOptions(
            directApplicationDirectory,
            arguments.ToArray());
    }
}
