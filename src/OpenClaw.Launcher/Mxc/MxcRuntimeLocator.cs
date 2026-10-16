using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Mxc;

/// <summary>
/// Provenance recorded when the pinned MXC runtime was staged into the package.
/// It is written by scripts\Get-MxcRuntime.ps1 from the verified NuGet archive
/// and read back so diagnostics can name the exact runtime in use.
/// </summary>
internal sealed record MxcRuntimeProvenance(
    string Package,
    string Version,
    string Architecture);

internal sealed record MxcRuntimeLocation(
    string Directory,
    string LibraryPath,
    MxcRuntimeProvenance? Provenance);

/// <summary>
/// Finds the MXC runtime staged beside the launcher.
/// </summary>
/// <remarks>
/// Resolution never searches PATH or an environment-selected location: the runtime is a
/// release trust-chain input, so an arbitrary native library found on the machine
/// must not be able to service a managed OpenClaw session.
/// </remarks>
internal static class MxcRuntimeLocator
{
    /// <summary>
    /// Directory name under the application base that holds the staged runtime.
    /// </summary>
    public const string RuntimeDirectoryName = "mxc";

    public const string NativeLibraryFileName = "mxc_ffi.dll";
    public const string ProvenanceFileName = "mxc-runtime.json";

    public const string RuntimeDirectoryVariable = "OPENCLAW_MXC_RUNTIME_DIR";

    public static MxcRuntimeLocation Locate() =>
        Locate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable);

    internal static MxcRuntimeLocation Locate(
        string baseDirectory,
        Func<string, string?> readEnvironmentVariable)
    {
        string? overrideDirectory =
            readEnvironmentVariable(RuntimeDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            throw new MxcException(
                MxcErrorCode.ConfigurationRejected,
                $"Remove {RuntimeDirectoryVariable} from your environment, open a new terminal, " +
                "and run `clawctl setup` again. The SDK provider only uses the packaged runtime.");
        }

        string directory = Path.GetFullPath(Path.Combine(
            baseDirectory,
            RuntimeDirectoryName,
            CurrentArchitectureName()));

        string libraryPath = Path.Combine(directory, NativeLibraryFileName);
        if (!File.Exists(libraryPath))
        {
            throw new MxcException(
                MxcErrorCode.RuntimeUnavailable,
                $"The MXC runtime is not available: {libraryPath} is missing.");
        }

        return new MxcRuntimeLocation(
            directory,
            libraryPath,
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

    private static readonly Lock RuntimeLock = new();
    private static string? _loadedPath;

    internal static void InitializeSdkRuntime(MxcRuntimeLocation location)
    {
        lock (RuntimeLock)
        {
            string libraryPath = Path.GetFullPath(location.LibraryPath);
            for (DirectoryInfo? directory = new(Path.GetDirectoryName(libraryPath)!);
                 directory is not null;
                 directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new MxcException(MxcErrorCode.RuntimeUnavailable,
                        "The packaged MXC runtime directory must not traverse reparse points.");
                }
            }
            if (_loadedPath is not null)
            {
                if (!string.Equals(_loadedPath, libraryPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MxcException(MxcErrorCode.RuntimeUnavailable, "The SDK runtime is already bound to another package path.");
                }
                return;
            }

            // Keep the verified file open without write/delete sharing until the image is mapped.
            using FileStream file = new(libraryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            (long length, string digest) = MxcRuntimeIdentity.ForArchitecture(CurrentArchitectureName());
            if (file.Length != length ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(file)), digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new MxcException(MxcErrorCode.RuntimeUnavailable, "The packaged MXC native library does not match its pinned identity.");
            }
            if ((File.GetAttributes(libraryPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new MxcException(MxcErrorCode.RuntimeUnavailable, "The packaged MXC native library must not be a reparse point.");
            }
            if (!OperatingSystem.IsWindows())
            {
                throw new MxcException(MxcErrorCode.RuntimeUnavailable, "This session provider requires Windows.");
            }

            // Only system DLLs may satisfy dependencies of the exact verified SDK image.
            nint handle = LoadLibraryEx(libraryPath, nint.Zero, 0x00000800);
            if (handle == nint.Zero)
            {
                throw new MxcException(MxcErrorCode.RuntimeUnavailable,
                    $"The packaged MXC native library could not load (Windows error {Marshal.GetLastWin32Error()}).");
            }
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Microsoft.Mxc.Sdk.V1.MxcProcess).Assembly,
                    (name, _, _) => name == "mxc_ffi" ? handle : nint.Zero);
            }
            catch
            {
                NativeLibrary.Free(handle);
                throw;
            }

            // SDK handles may outlive an invocation; this one mapping belongs to the process lifetime.
            _loadedPath = libraryPath;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string fileName, nint reserved, uint flags);

    private static MxcRuntimeProvenance? ReadProvenance(string directory)
    {
        string path = Path.Combine(directory, ProvenanceFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        // Diagnostics are not authority; initialization verifies the compiled identity.
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
