namespace OpenClaw.Launcher.Session;

/// <summary>Where the agent's <c>openclaw</c> command lives.</summary>
internal sealed record AgentTools(string DirectoryPath, string ShimPath);

/// <summary>Builds environment values consumed by the guest-side command shim.</summary>
internal static class AgentToolShim
{
    internal const string NodeVariable = "OPENCLAW_SHIM_NODE";
    internal const string EntryPointVariable = "OPENCLAW_SHIM_ENTRY";

    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        string nodePath,
        string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NodeVariable] = nodePath,
            [EntryPointVariable] = Path.Combine(applicationDirectory, "openclaw.mjs"),
        };
    }
}
