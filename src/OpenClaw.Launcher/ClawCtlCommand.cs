namespace OpenClaw.Launcher;

public enum ClawCtlCommand
{
    Help,
    Version,
    Setup,
    GatewayIsolationStatus,
    GatewayIsolationEnable,
    GatewayIsolationDisable
}

public sealed record ClawCtlCommandParseResult(
    ClawCtlCommand Command,
    string? Error = null);

public static class ClawCtlCommandParser
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

        if (IsPair(args, "gateway-isolation", "status"))
        {
            return new ClawCtlCommandParseResult(ClawCtlCommand.GatewayIsolationStatus);
        }

        if (IsPair(args, "gateway-isolation", "enable"))
        {
            return new ClawCtlCommandParseResult(ClawCtlCommand.GatewayIsolationEnable);
        }

        if (IsPair(args, "gateway-isolation", "disable"))
        {
            return new ClawCtlCommandParseResult(ClawCtlCommand.GatewayIsolationDisable);
        }

        return new ClawCtlCommandParseResult(
            ClawCtlCommand.Help,
            $"Unknown command or option: {string.Join(' ', args)}");
    }

    private static bool IsSingle(IReadOnlyList<string> args, string value) =>
        args.Count == 1 &&
        string.Equals(args[0], value, StringComparison.Ordinal);

    private static bool IsPair(
        IReadOnlyList<string> args,
        string first,
        string second) =>
        args.Count == 2 &&
        string.Equals(args[0], first, StringComparison.Ordinal) &&
        string.Equals(args[1], second, StringComparison.Ordinal);
}
