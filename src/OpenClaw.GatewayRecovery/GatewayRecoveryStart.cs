using System.ComponentModel;

namespace OpenClaw.GatewayRecovery;

/// <summary>
/// Starts a program without a console window, waits for it, and returns its
/// exit code.
/// </summary>
internal interface IHiddenProcessRunner
{
    int Run(string path, string arguments, string workingDirectory);
}

/// <summary>What the sign-in helper needs to know about the running installation.</summary>
/// <param name="PackageFamilyName">The installed package, or null when not packaged.</param>
/// <param name="PackageDirectory">The package root, which holds its manifest.</param>
/// <param name="LocalApplicationData">The signed-in user's local application data.</param>
/// <param name="WorkingDirectory">The directory the control command starts in.</param>
internal sealed record GatewayRecoveryEnvironment(
    string? PackageFamilyName,
    string PackageDirectory,
    string LocalApplicationData,
    string WorkingDirectory)
{
    public static GatewayRecoveryEnvironment Current() =>
        new(
            HostDataPaths.TryGetPackageFamilyName(),
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

/// <summary>
/// Runs <c>clawctl gateway-service start --recovery</c> for this installation
/// and returns its exit code, so the logon task's last-run result is the
/// recovery result rather than whether the helper started.
/// </summary>
/// <remarks>
/// The control command runs through this package's own package-qualified
/// <c>Control</c> alias, as an interactive <c>clawctl</c> would, so it keeps
/// its identity and its normal host diagnostics. Every outcome before that
/// command starts is logged here, because nothing else observes a sign-in.
/// </remarks>
internal static class GatewayRecoveryStart
{
    public const string ControlApplicationId = "Control";

    public const string ControlExecutable = "openclaw.exe";

    public const string ControlArguments = "gateway-service start --recovery";

    public const int FailureExitCode = 1;

    public static int Run(
        GatewayRecoveryEnvironment environment,
        IHiddenProcessRunner runner,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(log);

        if (environment.PackageFamilyName is null)
        {
            log("Sign-in helper: not running from the installed package, so the gateway was not started.");
            return FailureExitCode;
        }

        if (!PackageExecutionAlias.TryResolve(
                environment.PackageDirectory,
                environment.PackageFamilyName,
                ControlApplicationId,
                ControlExecutable,
                environment.LocalApplicationData,
                out string? controlPath,
                out string? error))
        {
            log($"Sign-in helper: the gateway was not started. {error}");
            return FailureExitCode;
        }

        if (!File.Exists(controlPath))
        {
            log(
                $"Sign-in helper: the gateway was not started because the package-qualified " +
                $"control alias '{controlPath}' is unavailable. Repair the package registration, " +
                "then run clawctl setup.");
            return FailureExitCode;
        }

        log($"Sign-in helper: starting the gateway through '{controlPath}'.");
        int exitCode;
        try
        {
            exitCode = runner.Run(controlPath, ControlArguments, environment.WorkingDirectory);
        }
        catch (Win32Exception exception)
        {
            log(
                $"Sign-in helper: '{controlPath}' could not be started: {exception.Message} " +
                $"(error {exception.NativeErrorCode}).");
            return FailureExitCode;
        }

        log($"Sign-in helper: the gateway start command exited with code {exitCode}.");
        return exitCode;
    }
}
