using System.Runtime.InteropServices;

namespace OpenClaw.Launcher;

/// <summary>
/// The running process's MSIX package identity.
/// </summary>
internal static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const int AppModelErrorNoApplication = 15703;

    /// <summary>
    /// Prefix required by MXC when a packaged caller provisions a sandbox.
    /// </summary>
    public const string ApplicationIdPrefix = "PFN:";

    /// <summary>
    /// The package family name, or null when running unpackaged.
    /// </summary>
    /// <remarks>
    /// Unpackaged is a normal development configuration, not an error, so it
    /// is reported as null rather than thrown.
    /// </remarks>
    public static string? TryGetPackageFamilyName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return TryGetPackageIdentity(GetCurrentPackageFamilyName);
    }

    public static string? TryGetApplicationUserModelId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return TryGetPackageIdentity(GetCurrentApplicationUserModelId);
    }

    private static string? TryGetPackageIdentity(
        PackageIdentityReader readIdentity)
    {
        uint length = 0;
        int result = readIdentity(ref length, null);
        if (result is AppModelErrorNoPackage or AppModelErrorNoApplication)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new InvalidOperationException(
                $"Unable to determine package identity (error {result}).");
        }

        var value = new char[length];
        result = readIdentity(ref length, value);
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Unable to determine package identity (error {result}).");
        }

        return new string(value, 0, checked((int)length - 1));
    }

    /// <summary>
    /// Builds the MXC application id for a package family name.
    /// </summary>
    /// <remarks>
    /// The backend fixes this value for the sandbox lifetime, so it must be the
    /// real family name of the calling package. This package and the internal
    /// MSIX therefore never share a session.
    /// </remarks>
    public static string ToApplicationId(string packageFamilyName) =>
        ApplicationIdPrefix + packageFamilyName;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(
        ref uint packageFamilyNameLength,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)]
        char[]? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentApplicationUserModelId(
        ref uint applicationUserModelIdLength,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)]
        char[]? applicationUserModelId);

    private delegate int PackageIdentityReader(
        ref uint length,
        char[]? value);
}

/// <summary>
/// Where this installation keeps its own writable state.
/// </summary>
/// <remarks>
/// Every writable path is derived here so nothing duplicates the packaged
/// versus unpackaged decision, and so tests can redirect the root instead of
/// touching real profile state.
/// </remarks>
internal sealed class HostPaths
{
    public const string UnpackagedDirectoryName = "OpenClawGatewayMSIX";

    private HostPaths(string stateRoot, string? packageFamilyName)
    {
        StateRoot = stateRoot;
        PackageFamilyName = packageFamilyName;
    }

    public string StateRoot { get; }

    public string? PackageFamilyName { get; }

    public string LogPath => Path.Combine(StateRoot, "Logs", "openclaw.log");

    public string PreResetReportPath =>
        Path.Combine(StateRoot, "Logs", "pre-reset.log");

    public string SessionStatePath => Path.Combine(StateRoot, "session.json");

    /// <summary>
    /// The marker written only after explicit setup completes all of its
    /// session, launch-configuration, and sign-in-recovery steps.
    /// </summary>
    public string SetupStatePath => Path.Combine(StateRoot, "setup.json");

    /// <summary>
    /// The script the logon task runs.
    /// </summary>
    /// <remarks>
    /// The task deliberately does not invoke the app alias directly. This file
    /// is rewritten without elevation on every install, whereas changing the
    /// task's own arguments requires re-registering it. Routing through it
    /// keeps the launch command free to change, and keeps the Startup-folder
    /// lane calling the same single definition instead of a second one that
    /// can drift.
    /// </remarks>
    public string GatewayLauncherPath =>
        Path.Combine(StateRoot, "gateway-launcher.cmd");

    /// <summary>
    /// Where the recorded gateway process and its persistence choices live.
    /// </summary>
    public string GatewayStatePath => Path.Combine(StateRoot, "gateway.json");

    public string GatewayGuidanceStatePath =>
        Path.Combine(StateRoot, "gateway-guidance.json");

    /// <summary>
    /// The host-owned copy of the generated PowerShell completion script.
    /// </summary>
    /// <remarks>
    /// The agent workspace is replaceable during setup.  Keep the authoritative
    /// cache in LocalState and project it into that workspace only for shells
    /// running in the current isolated session.
    /// </remarks>
    public string CompletionCachePath =>
        Path.Combine(StateRoot, "Completions", "openclaw.ps1");

    /// <summary>
    /// The gateway's launch configuration, kept separate from its recorded
    /// process so that stopping the gateway never discards the user's port.
    /// </summary>
    public string GatewayConfigurationPath =>
        Path.Combine(StateRoot, "gateway-config.json");

    public static HostPaths Create() =>
        Create(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            PackageIdentity.TryGetPackageFamilyName());

    internal static HostPaths Create(string localAppData, string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        // A packaged process writes inside its own LocalState so the state is
        // removed with the package and cannot collide with another
        // installation's.
        string stateRoot = packageFamilyName is null
            ? Path.Combine(localAppData, UnpackagedDirectoryName)
            : Path.Combine(
                localAppData,
                "Packages",
                packageFamilyName,
                "LocalState",
                UnpackagedDirectoryName);

        return new HostPaths(stateRoot, packageFamilyName);
    }

    /// <summary>
    /// Builds paths rooted at an arbitrary directory, for tests and diagnosis.
    /// </summary>
    internal static HostPaths ForRoot(string stateRoot, string? packageFamilyName = null) =>
        new(Path.GetFullPath(stateRoot), packageFamilyName);
}
