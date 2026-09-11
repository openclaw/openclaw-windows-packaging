namespace OpenClaw.Launcher;

public enum ClawCtlCommand
{
    Help,
    Version,
    Setup,
    SessionStatus,
    SessionStop,
    SessionRemove
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

        if (args.Count >= 1 &&
            string.Equals(args[0], "session", StringComparison.Ordinal))
        {
            return ParseSession(args);
        }

        return new ClawCtlCommandParseResult(
            ClawCtlCommand.Help,
            $"Unknown command or option: {string.Join(' ', args)}");
    }

    private static ClawCtlCommandParseResult ParseSession(
        IReadOnlyList<string> args)
    {
        // A bare `session` is reported as a missing sub-command rather than
        // silently defaulting to `status`, so a mistyped destructive verb can
        // never resolve to a different operation than the user typed.
        if (args.Count == 1)
        {
            return new ClawCtlCommandParseResult(
                ClawCtlCommand.Help,
                "`clawctl session` requires a sub-command: status, stop, or remove.");
        }

        if (args.Count > 2)
        {
            return new ClawCtlCommandParseResult(
                ClawCtlCommand.Help,
                "Unexpected arguments after " +
                $"`clawctl session {args[1]}`: {string.Join(' ', args.Skip(2))}");
        }

        return args[1] switch
        {
            "status" => new ClawCtlCommandParseResult(
                ClawCtlCommand.SessionStatus),
            "stop" => new ClawCtlCommandParseResult(ClawCtlCommand.SessionStop),
            "remove" => new ClawCtlCommandParseResult(
                ClawCtlCommand.SessionRemove),
            _ => new ClawCtlCommandParseResult(
                ClawCtlCommand.Help,
                $"Unknown session sub-command: {args[1]}")
        };
    }

    private static bool IsSingle(IReadOnlyList<string> args, string value) =>
        args.Count == 1 &&
        string.Equals(args[0], value, StringComparison.Ordinal);
}
