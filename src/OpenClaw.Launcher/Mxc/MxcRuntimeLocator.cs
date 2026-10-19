using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Mxc;

internal sealed record MxcRuntimeProvenance(
    string Package,
    string Version,
    string Architecture);

internal sealed record MxcRuntimeLocation(
    string Directory,
    string NativeLibraryPath,
    string PackageLifecyclePath,
    MxcRuntimeProvenance? Provenance);

internal static class MxcRuntimeLocator
{
    public const string NativeLibraryFileName = "mxc_ffi.dll";
    public const string PackageLifecycleFileName = "plm.exe";
    public const string ProvenanceFileName = "mxc-runtime.json";
    public const string NativeDirectoryOverrideVariable = "MXC_FFI_DIR";

    private static readonly string[] NativeFiles =
        [NativeLibraryFileName, PackageLifecycleFileName, "wslcsdk.dll", "wxc-wslc-daemon.exe"];
    private static readonly Lock LoadLock = new();
    private static nint _module;
    private static string? _loadedPath;

    public static MxcRuntimeLocation Locate() => Locate(AppContext.BaseDirectory);

    internal static MxcRuntimeLocation Locate(string baseDirectory)
    {
        string root = Path.GetFullPath(baseDirectory);
        string directory = File.Exists(Path.Combine(root, NativeLibraryFileName))
            ? root
            : Path.Combine(root, "runtimes", $"win-{CurrentArchitectureName()}", "native");

        foreach (string name in NativeFiles)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                throw new MxcException(
                    MxcErrorCode.RuntimeUnavailable,
                    $"The MXC runtime is incomplete: {path} is missing.");
            }
        }

        return new MxcRuntimeLocation(
            directory,
            Path.Combine(directory, NativeLibraryFileName),
            Path.Combine(directory, PackageLifecycleFileName),
            ReadProvenance(root));
    }

    internal static void EnsureLoaded(MxcRuntimeLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        lock (LoadLock)
        {
            if (_module != 0)
            {
                if (!string.Equals(_loadedPath, location.NativeLibraryPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MxcException(
                        MxcErrorCode.RuntimeUnavailable,
                        "This process has already loaded a different MXC native runtime.");
                }
                return;
            }

            // Bind SDK imports to this exact image and constrain dependency
            // search for predictable loading. Keep the mapping for the process
            // lifetime because SDK handles can outlive an invocation.
            Environment.SetEnvironmentVariable(NativeDirectoryOverrideVariable, null);
            nint module = NativeLibrary.Load(
                location.NativeLibraryPath,
                typeof(MxcRuntimeLocator).Assembly,
                DllImportSearchPath.System32);
            try
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(Microsoft.Mxc.Sdk.V1.MxcProcess).Assembly,
                    (name, _, _) => name == "mxc_ffi" ? module : nint.Zero);
                _module = module;
            }
            finally
            {
                if (_module == 0)
                {
                    NativeLibrary.Free(module);
                }
            }
            _loadedPath = location.NativeLibraryPath;
        }
    }

    internal static string CurrentArchitectureName() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
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

        // Descriptive metadata is not an authorization input.
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
