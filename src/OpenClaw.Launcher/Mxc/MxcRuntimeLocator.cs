using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Mxc;

/// <summary>
/// Provenance recorded when the pinned MXC runtime was staged into the package.
/// It is written by scripts\Get-MxcRuntime.ps1 from the verified npm archive
/// and read back so diagnostics can name the exact runtime in use.
/// </summary>
internal sealed record MxcRuntimeProvenance(
    string Package,
    string Version,
    string Architecture);

internal sealed record MxcRuntimeLocation(
    string Directory,
    string ExecutorPath,
    string PackageLifecyclePath,
    MxcRuntimeProvenance? Provenance);

/// <summary>
/// Finds the MXC runtime staged beside the launcher.
/// </summary>
/// <remarks>
/// Resolution never searches PATH or a user-writable location: the runtime is a
/// release trust-chain input, so an arbitrary wxc-exec.exe found on the machine
/// must not be able to service a managed OpenClaw session.
/// </remarks>
internal static class MxcRuntimeLocator
{
    /// <summary>
    /// Directory name under the application base that holds the staged runtime.
    /// </summary>
    public const string RuntimeDirectoryName = "mxc";

    public const string ExecutorFileName = "wxc-exec.exe";
    public const string PackageLifecycleFileName = "plm.exe";
    public const string ProvenanceFileName = "mxc-runtime.json";

    /// <summary>
    /// Development and compatibility-experiment override naming a directory
    /// that already contains a verified runtime layout.
    /// </summary>
    public const string RuntimeDirectoryVariable = "OPENCLAW_MXC_RUNTIME_DIR";

    public static MxcRuntimeLocation Locate() =>
        Locate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable);

    internal static MxcRuntimeLocation Locate(
        string baseDirectory,
        Func<string, string?> readEnvironmentVariable)
    {
        string? overrideDirectory =
            readEnvironmentVariable(RuntimeDirectoryVariable);
        string directory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                baseDirectory,
                RuntimeDirectoryName,
                CurrentArchitectureName())
            : overrideDirectory;

        string executorPath = Path.Combine(directory, ExecutorFileName);
        if (!File.Exists(executorPath))
        {
            throw new MxcException(
                MxcErrorCode.RuntimeUnavailable,
                $"The MXC runtime is not available: {executorPath} is missing.");
        }

        string packageLifecyclePath =
            Path.Combine(directory, PackageLifecycleFileName);
        if (!File.Exists(packageLifecyclePath))
        {
            throw new MxcException(
                MxcErrorCode.RuntimeUnavailable,
                "The MXC runtime is incomplete: " +
                $"{packageLifecyclePath} is missing.");
        }

        return new MxcRuntimeLocation(
            directory,
            executorPath,
            packageLifecyclePath,
            ReadProvenance(directory));
    }

    /// <summary>
    /// Runtime identifier fragment naming the architecture-specific staging
    /// directory. The process architecture is used rather than the OS
    /// architecture so an x64 launcher emulated on ARM64 loads the matching
    /// runtime instead of one it cannot execute.
    /// </summary>
    internal static string CurrentArchitectureName() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            var other => throw new MxcException(
                MxcErrorCode.RuntimeUnavailable,
                $"MXC does not ship a runtime for {other}.")
        };

    private static MxcRuntimeProvenance? ReadProvenance(string directory)
    {
        string path = Path.Combine(directory, ProvenanceFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        // Provenance is descriptive metadata for diagnostics. A damaged file
        // must not block a runtime whose binaries were already verified at
        // staging time, so report unknown provenance instead of failing.
        try
        {
            MxcRuntimeProvenancePayload? payload = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                MxcRuntimeJsonContext.Default.MxcRuntimeProvenancePayload);
            return payload is null ||
                string.IsNullOrWhiteSpace(payload.Package) ||
                string.IsNullOrWhiteSpace(payload.Version) ||
                string.IsNullOrWhiteSpace(payload.Architecture)
                    ? null
                    : new MxcRuntimeProvenance(
                        payload.Package,
                        payload.Version,
                        payload.Architecture);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed class MxcRuntimeProvenancePayload
{
    [JsonPropertyName("package")]
    public string? Package { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; set; }
}

[JsonSerializable(typeof(MxcRuntimeProvenancePayload))]
internal sealed partial class MxcRuntimeJsonContext : JsonSerializerContext;
