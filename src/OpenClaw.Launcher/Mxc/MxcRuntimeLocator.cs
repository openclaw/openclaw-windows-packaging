using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Mxc;

internal sealed record MxcRuntimeProvenance(string Package, string Version, string Architecture);

internal sealed record MxcRuntimeLocation(string Directory, MxcRuntimeProvenance? Provenance);

internal static class MxcRuntimeLocator
{
    public const string ProvenanceFileName = "mxc-runtime.json";

    public static MxcRuntimeLocation Locate() => Locate(AppContext.BaseDirectory);

    internal static MxcRuntimeLocation Locate(string baseDirectory)
    {
        string root = Path.GetFullPath(baseDirectory);
        return new MxcRuntimeLocation(root, ReadProvenance(root));
    }

    internal static string CurrentArchitectureName() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString()
        };

    private static MxcRuntimeProvenance? ReadProvenance(string directory)
    {
        string path = Path.Combine(directory, ProvenanceFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        // Descriptive package metadata is not a native-loading or authorization input.
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
                    : new MxcRuntimeProvenance(payload.Package, payload.Version, payload.Architecture);
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
