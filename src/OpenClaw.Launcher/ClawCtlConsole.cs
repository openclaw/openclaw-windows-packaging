using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher;

public static class ClawCtlConsole
{
    public static void WriteHelp(TextWriter output)
    {
        output.WriteLine("clawctl - OpenClaw package preparation");
        output.WriteLine();
        WriteUsage(output);
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  setup            Prepare the packaged OpenClaw environment.");
        output.WriteLine("  session status   Show the recorded isolated session.");
        output.WriteLine("  session stop     Stop the isolated session, keeping its data.");
        output.WriteLine("  session remove   Remove the isolated session and its guest data.");
        output.WriteLine();
        output.WriteLine("  gateway-service install    Start the gateway and restart it at sign-in.");
        output.WriteLine("  gateway-service status     Show the gateway and its sign-in recovery.");
        output.WriteLine("  gateway-service start      Start the gateway if it is not running.");
        output.WriteLine("  gateway-service stop       Stop the gateway, keeping its data.");
        output.WriteLine("  gateway-service uninstall  Stop the gateway and remove sign-in recovery.");
        output.WriteLine("  gateway-service diagnose   Explain why the gateway is or is not running.");
        output.WriteLine();
        output.WriteLine(
            "`setup`, `session status`, and `gateway-service status` are " +
            "read-only. They report Node.js, packaged application, isolated " +
            "session, and gateway prerequisites without changing them.");
        output.WriteLine();
        WriteNodePrerequisite(output);
        output.WriteLine();
        output.WriteLine("Run `openclaw <arguments>` to invoke the OpenClaw CLI.");
    }

    public static void WriteUsage(TextWriter output) =>
        output.WriteLine(
            "Usage: clawctl setup | session <status|stop|remove> | " +
            "gateway-service <install|status|start|stop|uninstall|diagnose>");

    public static void WriteNodePrerequisite(TextWriter output)
    {
        output.WriteLine(
            $"Prerequisite: install Node.js {NodeRuntimeResolver.SupportedVersions}.");
        output.WriteLine($"  {NodeRuntimeResolver.InstallCommand}");
    }

    internal static void WriteNodeRuntimeSummary(
        TextWriter output,
        NodeRuntime runtime) =>
        output.WriteLine(
            $"Using Node.js {runtime.Version} from {runtime.ExecutablePath}");

    public static void WriteReadinessSummary(
        TextWriter output,
        string applicationDirectory)
    {
        output.WriteLine();
        output.WriteLine("OpenClaw package is ready.");
        output.WriteLine($"Read-only application files: {applicationDirectory}");
    }

    /// <summary>
    /// Reports MXC session prerequisites without provisioning anything.
    /// </summary>
    /// <remarks>
    /// Missing prerequisites are reported as an isolated-session limitation
    /// rather than an overall setup failure: OpenClaw still runs, so
    /// <c>setup</c> keeps its existing exit code and its existing read-only
    /// contract.
    /// </remarks>
    public static void WriteMxcReadinessSummary(
        TextWriter output,
        MxcReadinessReport report)
    {
        output.WriteLine();
        output.WriteLine("Isolated session support:");

        if (report.RuntimeAvailable)
        {
            string runtime = report.Provenance is { } provenance
                ? $"{provenance.Package} {provenance.Version} ({provenance.Architecture})"
                : "unknown version";
            output.WriteLine($"  MXC runtime: {runtime}");
            output.WriteLine($"  Runtime files: {report.RuntimeDirectory}");
        }
        else
        {
            output.WriteLine($"  MXC runtime: unavailable. {report.RuntimeUnavailableReason}");
        }

        switch (report.SupportEvidence)
        {
            case MxcSupportEvidence.BackendProbe:
                // Measured, so report what the host actually supports rather
                // than what its build number predicts.
                output.WriteLine(
                    report.HostSupport == MxcHostSupport.Supported
                        ? "  Backend: the isolated-session backend is available."
                        : "  Backend: the isolated-session backend is not " +
                          "available on this host.");
                if (report.BackendProbe is { Tier: { Length: > 0 } tier })
                {
                    output.WriteLine($"  Backend tier: {tier}");
                }

                foreach (string warning in report.BackendProbe?.Warnings ?? [])
                {
                    output.WriteLine($"  Backend warning: {warning}");
                }

                break;

            case MxcSupportEvidence.HostBuild:
                output.WriteLine(
                    report.HostSupport == MxcHostSupport.Supported
                        ? $"  Windows build: {report.HostBuild} meets the " +
                          $"{MxcReadiness.MinimumHostBuild} minimum (the " +
                          "backend itself could not be queried)."
                        : $"  Windows build: {report.HostBuild} is below the " +
                          $"{MxcReadiness.MinimumHostBuild} minimum.");
                break;

            default:
                output.WriteLine(
                    "  Windows build: could not be determined; the minimum is " +
                    $"{MxcReadiness.MinimumHostBuild}.");
                break;
        }

        if (report.BackendProbeFailureReason is { Length: > 0 } probeFailure)
        {
            output.WriteLine($"  Backend probe failed: {probeFailure}");
        }

        if (!report.RuntimeAvailable || report.HostSupport != MxcHostSupport.Supported)
        {
            output.WriteLine(
                "  Isolated sessions are unavailable on this machine.");
        }
    }

    /// <summary>
    /// Reports the recorded session without contacting the backend.
    /// </summary>
    /// <remarks>
    /// Recorded identity and live evidence are reported separately on purpose.
    /// The backend offers no authoritative session enumeration, so claiming a
    /// session is "running" here would be a guess presented as a fact.
    /// </remarks>
    public static void WriteSessionStatus(
        TextWriter output,
        SessionStatus status)
    {
        switch (status.Availability)
        {
            case SessionAvailability.None:
                output.WriteLine("No isolated session is recorded.");
                output.WriteLine(
                    "  Running `openclaw` creates one on a supported machine.");
                return;

            case SessionAvailability.Unusable:
                output.WriteLine("The recorded isolated session is unusable.");
                output.WriteLine($"  Reason: {status.Detail}");
                output.WriteLine(
                    "  Run `clawctl session remove` to discard it and start over.");
                return;
        }

        SessionRecord record = status.Record!;
        output.WriteLine("An isolated session is recorded for this installation.");
        output.WriteLine($"  Recorded: {record.CreatedUtc:u}");
        if (record.AgentUserName is { Length: > 0 } agent)
        {
            output.WriteLine($"  Guest account: {agent}");
        }

        if (record.WorkspacePath is { Length: > 0 } workspace)
        {
            output.WriteLine($"  Shared workspace: {workspace}");
        }

        output.WriteLine(
            "  Live state is not queried; the backend does not report it.");
    }

    public static void WriteSessionStopped(TextWriter output, bool stopped) =>
        output.WriteLine(
            stopped
                ? "Stopped the isolated session. Its profile and data are kept."
                : "No isolated session is recorded, so there was nothing to stop.");

    public static void WriteSessionRemoved(
        TextWriter output,
        SessionRemovalResult result)
    {
        if (!result.Removed)
        {
            output.WriteLine(
                "No isolated session is recorded, so there was nothing to remove.");
            return;
        }

        if (result.StopFailure is { Length: > 0 } stopFailure)
        {
            output.WriteLine($"Warning: stopping the session failed: {stopFailure}");
            output.WriteLine("  Removal continued so the guest account is released.");
        }

        output.WriteLine("Removed the isolated session.");
        output.WriteLine(
            "  Its guest profile and shared workspace contents are gone.");
    }

    /// <summary>
    /// Reports the gateway, keeping observed state and configured recovery
    /// separate.
    /// </summary>
    /// <remarks>
    /// "Not running" and "could not be determined" are never merged. A user who
    /// is told the gateway is stopped will start another one, which is exactly
    /// the wrong move when the truth is that nothing could be observed.
    /// </remarks>
    public static void WriteGatewayStatus(
        TextWriter output,
        GatewayStatusReport report,
        GatewayPersistenceStatus? persistence)
    {
        switch (report.State)
        {
            case GatewayState.NotStarted:
                output.WriteLine("No gateway has been started.");
                output.WriteLine(
                    "  Run `clawctl gateway-service install` to start it and " +
                    "restart it at sign-in.");
                break;

            case GatewayState.Running:
                output.WriteLine(report.Message);
                if (report.Record is { } running)
                {
                    output.WriteLine($"  Started: {running.StartedUtc:u}");
                    if (running.LogPath is { Length: > 0 } log)
                    {
                        output.WriteLine($"  Log: {log}");
                    }
                }

                break;

            case GatewayState.Stopped:
                output.WriteLine("The gateway is not running.");
                if (report.Detail is { Length: > 0 } stoppedDetail)
                {
                    output.WriteLine($"  {stoppedDetail}");
                }

                output.WriteLine("  Run `clawctl gateway-service start` to start it.");
                break;

            case GatewayState.Unhealthy:
                output.WriteLine("The gateway is running but is not serving.");
                if (report.Detail is { Length: > 0 } unhealthyDetail)
                {
                    output.WriteLine($"  {unhealthyDetail}");
                }

                if (report.Record?.LogPath is { Length: > 0 } unhealthyLog)
                {
                    output.WriteLine($"  Log: {unhealthyLog}");
                }

                output.WriteLine(
                    "  Run `clawctl gateway-service stop` and then `start`.");
                break;

            case GatewayState.Unknown:
                output.WriteLine("The gateway's state could not be determined.");
                if (report.Detail is { Length: > 0 } unknownDetail)
                {
                    output.WriteLine($"  {unknownDetail}");
                }

                output.WriteLine(
                    "  This is not the same as stopped. Starting another " +
                    "gateway could leave two contending for one port.");
                break;
        }

        if (persistence is not null)
        {
            output.WriteLine();
            WriteGatewayPersistence(output, persistence);
        }
    }

    public static void WriteGatewayPersistence(
        TextWriter output,
        GatewayPersistenceStatus status)
    {
        output.WriteLine($"Sign-in recovery: {Describe(status.State)}");

        if (status.Lane == GatewayPersistenceLane.StartupFolderFallback)
        {
            output.WriteLine("  Configured through the Startup folder.");
        }

        if (status.Detail is { Length: > 0 } detail)
        {
            output.WriteLine($"  {detail}");
        }

        // Drift is reported with the command that repairs it rather than
        // silently repaired behind an unexpected prompt.
        if (status.Remediation is { Length: > 0 } remediation &&
            status.State != GatewayPersistenceState.Ready)
        {
            output.WriteLine($"  Repair with: {remediation}");
        }
    }

    public static void WriteGatewayStarted(
        TextWriter output,
        GatewayStartResult result)
    {
        output.WriteLine(result.Message);

        if (result.Record.LogPath is { Length: > 0 } log)
        {
            output.WriteLine($"  Log: {log}");
        }

        if (result.Record.AutostartDisabled)
        {
            output.WriteLine(
                "  Sign-in recovery stays off because it was explicitly " +
                "disabled. Run `clawctl gateway-service install` to enable it.");
            return;
        }

        if (result.Persistence is { } persistence)
        {
            output.WriteLine();
            output.WriteLine($"Sign-in recovery: {Describe(persistence.State)}");
            if (persistence.Detail is { Length: > 0 } detail)
            {
                output.WriteLine($"  {detail}");
            }
        }
    }

    public static void WriteGatewayStopped(TextWriter output, GatewayStopResult result)
    {
        output.WriteLine(result.Message);

        if (result.Detail is { Length: > 0 } detail)
        {
            output.WriteLine($"  {detail}");
        }
    }

    public static void WriteGatewayUninstalled(
        TextWriter output,
        GatewayStopResult stop,
        GatewayPersistenceRemovalResult removal)
    {
        output.WriteLine(stop.Message);
        if (stop.Detail is { Length: > 0 } stopDetail)
        {
            output.WriteLine($"  {stopDetail}");
        }

        output.WriteLine(removal.Message);
        if (removal.Detail is { Length: > 0 } removalDetail)
        {
            output.WriteLine($"  {removalDetail}");
        }

        output.WriteLine(
            "  The isolated session and its data are kept. Use " +
            "`clawctl session remove` to discard those.");
    }

    private static string Describe(GatewayPersistenceState state) => state switch
    {
        GatewayPersistenceState.Ready => "configured",
        GatewayPersistenceState.NotInstalled => "not configured",
        GatewayPersistenceState.ActionRequired => "needs attention",
        _ => "could not be determined"
    };

    /// <summary>
    /// Reports that the gateway stack could not even be assembled.
    /// </summary>
    /// <remarks>
    /// Written as a diagnostic rather than thrown as an error, because this is
    /// the answer <c>diagnose</c> was asked for: the chain broke at its first
    /// link.
    /// </remarks>
    internal static void WriteGatewayUnavailable(TextWriter output, string reason)
    {
        output.WriteLine("Gateway diagnostics");
        output.WriteLine();
        output.WriteLine($"  [FAIL] Gateway: {reason}");
        output.WriteLine();
        output.WriteLine(
            "No further checks were possible. Install the OpenClaw package " +
            "and run this from its `clawctl` alias.");
    }

    /// <summary>
    /// Prints the whole chain, so a user can see which link broke without
    /// attaching a debugger.
    /// </summary>
    internal static void WriteGatewayDiagnostics(
        TextWriter output,
        GatewayDiagnosticReport report)
    {
        output.WriteLine("Gateway diagnostics");
        output.WriteLine();

        foreach (GatewayDiagnostic check in report.Checks)
        {
            // Unknown is its own mark. Rendering it as a failure would send the
            // user chasing a problem that may not exist.
            string mark = check.Ok switch
            {
                true => "ok  ",
                false => "FAIL",
                _ => "?   "
            };

            output.WriteLine($"  [{mark}] {check.Name}: {check.Detail}");
        }

        output.WriteLine();
        WriteGatewayStatus(output, report.Status, report.Persistence);

        if (report.LogPath is { Length: > 0 } log)
        {
            output.WriteLine();
            output.WriteLine($"Gateway log: {log}");
        }
    }
}
