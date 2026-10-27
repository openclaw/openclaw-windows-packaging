using System.Diagnostics.CodeAnalysis;

namespace OpenClaw.GatewayRecovery;

/// <summary>
/// The windowless sign-in helper. Both logon recovery lanes, the logon task
/// and the Startup-folder shortcut, start this executable through its
/// package-qualified execution alias.
/// </summary>
internal static class Program
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "This is the process last-chance handler of a process nobody watches. " +
            "An escaping exception would end in an unexplained exit or a crash " +
            "report instead of a host log entry and exit code 1.")]
    private static int Main()
    {
        using HostDiagnosticLog? log = TryCreateLog();
        void Write(string message)
        {
            try
            {
                log?.Write(message);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
            }
        }

        try
        {
            return GatewayRecoveryStart.Run(
                GatewayRecoveryEnvironment.Current(),
                new HiddenProcessRunner(),
                Write);
        }
        catch (Exception exception)
        {
            Write($"Sign-in helper: unhandled failure: {exception}");
            return GatewayRecoveryStart.FailureExitCode;
        }
    }

    private static HostDiagnosticLog? TryCreateLog()
    {
        try
        {
            return HostDiagnosticLog.Create();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
