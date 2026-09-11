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
        output.WriteLine(
            "`setup` and `session status` are read-only. They report Node.js, " +
            "packaged application, and isolated session prerequisites without " +
            "changing them.");
        output.WriteLine();
        WriteNodePrerequisite(output);
        output.WriteLine();
        output.WriteLine("Run `openclaw <arguments>` to invoke the OpenClaw CLI.");
    }

    public static void WriteUsage(TextWriter output) =>
        output.WriteLine("Usage: clawctl setup | session <status|stop|remove>");

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
}
