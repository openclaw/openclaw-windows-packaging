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
        output.WriteLine("  gateway-isolation status   Show the requested isolation mode.");
        output.WriteLine("  gateway-isolation enable   Apply isolation after the next manual Gateway restart.");
        output.WriteLine("  gateway-isolation disable  Disable isolation after the next manual Gateway restart.");
        output.WriteLine();
        WriteNodePrerequisite(output);
        output.WriteLine();
        output.WriteLine("Run `openclaw <arguments>` to invoke the OpenClaw CLI.");
    }

    public static void WriteUsage(TextWriter output) =>
        output.WriteLine("Usage: clawctl <setup|gateway-isolation>");

    public static void WriteGatewayIsolationStatus(
        TextWriter output,
        GatewayIsolationState state) =>
        output.WriteLine(
            $"Gateway isolation is requested: {(state.Enabled ? "enabled" : "disabled")}. " +
            "Changes take effect after the next manual Gateway restart.");

    public static void WriteGatewayIsolationUpdated(
        TextWriter output,
        GatewayIsolationState state) =>
        output.WriteLine(
            $"Gateway isolation has been {(state.Enabled ? "enabled" : "disabled")} for the next manual Gateway restart.");

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
}
