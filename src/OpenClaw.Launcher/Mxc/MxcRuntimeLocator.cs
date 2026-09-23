using System.Runtime.InteropServices;

namespace OpenClaw.Launcher.Mxc;

internal sealed record MxcRuntimeLocation(
    string Directory,
    string NativeLibraryPath,
    string PackageLifecyclePath);

/// <summary>
/// Verifies the MXC native unit staged beside the launcher and pins the SDK to
/// it.
/// </summary>
/// <remarks>
/// The native unit is a release trust-chain input, so a copy found elsewhere on
/// the machine must not be able to service a managed OpenClaw session. The SDK
/// loads <c>mxc_ffi.dll</c> from the application base, but it probes a
/// developer override variable first; that variable is cleared from this
/// process before the SDK's first native call.
/// </remarks>
internal static class MxcRuntimeLocator
{
    public const string NativeLibraryFileName = "mxc_ffi.dll";

    /// <summary>
    /// Helper the MXC engine resolves beside the loaded native library.
    /// </summary>
    public const string PackageLifecycleFileName = "plm.exe";

    /// <summary>
    /// SDK developer override that would otherwise redirect native loading to
    /// an arbitrary directory.
    /// </summary>
    public const string NativeDirectoryOverrideVariable = "MXC_FFI_DIR";

    public static MxcRuntimeLocation Locate() =>
        Locate(
            AppContext.BaseDirectory,
            static name => Environment.SetEnvironmentVariable(name, null));

    internal static MxcRuntimeLocation Locate(
        string baseDirectory,
        Action<string> clearEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(clearEnvironmentVariable);

        string directory = Path.GetFullPath(baseDirectory);
        string nativeLibraryPath = Path.Combine(directory, NativeLibraryFileName);
        if (!File.Exists(nativeLibraryPath))
        {
            throw new MxcException(
                MxcErrorCode.RuntimeUnavailable,
                $"The MXC runtime is not available: {nativeLibraryPath} is missing.");
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

        clearEnvironmentVariable(NativeDirectoryOverrideVariable);
        return new MxcRuntimeLocation(
            directory,
            nativeLibraryPath,
            packageLifecyclePath);
    }

    /// <summary>
    /// Runtime identifier fragment naming architecture-specific package
    /// content. The process architecture is used rather than the OS
    /// architecture so an x64 launcher emulated on ARM64 selects content it can
    /// execute.
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
}
