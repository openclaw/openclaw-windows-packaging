using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Gateway;

internal enum GatewayAutoAction
{
    Silent,
    Hint,
    Start,
}

internal static class AutoBehaviorPolicy
{
    public static bool IsAutomaticSetupEnabled(
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        return !IsSuppressed(
            readEnvironmentVariable(OpenClawRuntimeEnvironment.AutoSetupVariable));
    }

    public static GatewayAutoAction DecideGatewayAction(
        int openClawExitCode,
        bool interactive,
        SessionConfigReadinessState readiness,
        GatewayState gatewayState,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        bool eligible = openClawExitCode == 0 &&
            interactive &&
            readiness == SessionConfigReadinessState.StartupEligible &&
            gatewayState is GatewayState.NotStarted or GatewayState.Stopped;
        if (!eligible)
        {
            // A state that may still be healthy must not trigger a competing start.
            return GatewayAutoAction.Silent;
        }

        return IsSuppressed(
            readEnvironmentVariable(OpenClawRuntimeEnvironment.AutoGatewayStartVariable))
            ? GatewayAutoAction.Hint
            : GatewayAutoAction.Start;
    }

    private static bool IsSuppressed(string? value) =>
        value?.Trim().ToUpperInvariant() is "0" or "FALSE" or "NO" or "OFF";
}
