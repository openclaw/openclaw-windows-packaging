namespace OpenClaw.Launcher;

internal enum ClawCtlCommand
{
    Help,
    Version,
    Setup
}

internal sealed record ClawCtlCommandParseResult(
    ClawCtlCommand Command,
    string? Error = null);

internal static class ClawCtlCommandParser
{
    public static ClawCtlCommandParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || IsSingle(args, "--help") || IsSingle(args, "-h"))
        {
            return new ClawCtlCommandParseResult(ClawCtlCommand.Help);
        }

        if (IsSingle(args, "--version"))
        {
            return new ClawCtlCommandParseResult(ClawCtlCommand.Version);
        }

        if (IsSingle(args, "setup"))
        {
            return new ClawCtlCommandParseResult(ClawCtlCommand.Setup);
        }

        return new ClawCtlCommandParseResult(
            ClawCtlCommand.Help,
            $"Unknown command or option: {string.Join(' ', args)}");
    }

    private static bool IsSingle(IReadOnlyList<string> args, string value) =>
        args.Count == 1 &&
        string.Equals(args[0], value, StringComparison.Ordinal);
}
