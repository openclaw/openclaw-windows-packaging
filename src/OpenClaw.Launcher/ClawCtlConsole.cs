using OpenClaw.Launcher.Mxc;

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
        output.WriteLine("  setup   Prepare the packaged OpenClaw environment.");
        output.WriteLine();
        output.WriteLine(
            "`setup` is read-only. It reports Node.js, packaged application, " +
            "and isolated session prerequisites without changing them.");
        output.WriteLine();
        WriteNodePrerequisite(output);
        output.WriteLine();
        output.WriteLine("Run `openclaw <arguments>` to invoke the OpenClaw CLI.");
    }

    public static void WriteUsage(TextWriter output) =>
        output.WriteLine("Usage: clawctl setup");

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

        switch (report.HostSupport)
        {
            case MxcHostSupport.Supported:
                output.WriteLine(
                    $"  Windows build: {report.HostBuild} meets the " +
                    $"{MxcReadiness.MinimumHostBuild} minimum.");
                break;
            case MxcHostSupport.Unsupported:
                output.WriteLine(
                    $"  Windows build: {report.HostBuild} is below the " +
                    $"{MxcReadiness.MinimumHostBuild} minimum.");
                break;
            default:
                output.WriteLine(
                    "  Windows build: could not be determined; the minimum is " +
                    $"{MxcReadiness.MinimumHostBuild}.");
                break;
        }

        if (!report.RuntimeAvailable || report.HostSupport != MxcHostSupport.Supported)
        {
            output.WriteLine(
                "  Isolated sessions are unavailable on this machine.");
        }
    }
}
