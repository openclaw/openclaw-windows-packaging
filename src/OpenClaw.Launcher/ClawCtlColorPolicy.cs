namespace OpenClaw.Launcher;

internal static class ClawCtlColorPolicy
{
    internal static bool PrepareOutput(
        bool noColor,
        bool outputIsProcessConsoleWriter,
        bool consoleIsInteractive,
        Func<string, string?> readEnvironmentVariable,
        Func<bool> enableVirtualTerminalProcessing)
    {
        ArgumentNullException.ThrowIfNull(enableVirtualTerminalProcessing);

        bool useColor = ShouldUseColor(
            noColor,
            outputIsProcessConsoleWriter,
            consoleIsInteractive,
            readEnvironmentVariable);

        return useColor &&
            (!outputIsProcessConsoleWriter ||
             !consoleIsInteractive ||
             enableVirtualTerminalProcessing());
    }

    internal static bool ShouldUseColor(
        bool noColor,
        bool outputIsConsole,
        bool consoleIsInteractive,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        if (noColor || readEnvironmentVariable("NO_COLOR") is not null)
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
