namespace OpenClaw.Launcher;

internal static class ClawCtlColorPolicy
{
    internal static bool PrepareOutput(
        bool noColor,
        bool json,
        bool outputIsProcessConsoleWriter,
        bool consoleIsInteractive,
        Func<string, string?> readEnvironmentVariable,
        Func<bool> enableVirtualTerminalProcessing)
    {
        ArgumentNullException.ThrowIfNull(enableVirtualTerminalProcessing);

        bool useColor = ShouldUseColor(
            noColor,
            json,
            outputIsProcessConsoleWriter,
            consoleIsInteractive,
            readEnvironmentVariable);

        return useColor &&
            (!outputIsProcessConsoleWriter ||
             !consoleIsInteractive ||
             enableVirtualTerminalProcessing());
    }

    internal static bool PrepareForegroundOutput(
        bool noColor,
        bool json,
        bool outputIsProcessConsoleWriter,
        bool invocationIsInteractive,
        bool selectedStreamIsInteractive,
        Func<string, string?> readEnvironmentVariable,
        Func<bool> enableVirtualTerminalProcessing)
    {
        ArgumentNullException.ThrowIfNull(enableVirtualTerminalProcessing);

        bool useColor = ShouldUseColor(
            noColor,
            json,
            outputIsProcessConsoleWriter,
            invocationIsInteractive,
            readEnvironmentVariable);

        return useColor &&
            (!outputIsProcessConsoleWriter ||
             !selectedStreamIsInteractive ||
             enableVirtualTerminalProcessing());
    }

    internal static bool ShouldUseColor(
        bool noColor,
        bool json,
        bool outputIsConsole,
        bool consoleIsInteractive,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        if (noColor || json || readEnvironmentVariable("NO_COLOR") is not null)
        {
            return false;
        }

        string? forceColor = readEnvironmentVariable("FORCE_COLOR");
        if (forceColor is not null)
        {
            return !string.Equals(forceColor, "0", StringComparison.Ordinal);
        }

        if (readEnvironmentVariable("CI") is not null)
        {
            return false;
        }

        return outputIsConsole && consoleIsInteractive;
    }
}
