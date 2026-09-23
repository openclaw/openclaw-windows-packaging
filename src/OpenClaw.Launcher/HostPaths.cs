using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OpenClaw.Launcher;

/// <summary>
/// Where the installed package came from, as Windows classified it at
/// deployment. Values mirror <c>PackageOrigin</c> in appmodel.h.
/// </summary>
internal enum PackageOrigin
{
    Unknown = 0,
    Unsigned = 1,
    Inbox = 2,
    Store = 3,
    DeveloperUnsigned = 4,
    DeveloperSigned = 5,
    LineOfBusiness = 6
}

/// <summary>
/// How the running package was installed. A null member could not be read.
/// </summary>
/// <param name="FullName">The package full name, carrying version and architecture.</param>
/// <param name="Origin">The signature origin Windows recorded for the install.</param>
/// <param name="DevelopmentMode">
/// Whether the package was registered from a loose layout in Developer Mode.
/// </param>
/// <param name="InstallPath">The directory the package runs from.</param>
internal sealed record PackageProvenance(
    string FullName,
    PackageOrigin? Origin,
    bool? DevelopmentMode,
    string? InstallPath);

/// <summary>
/// The running process's MSIX package identity.
/// </summary>
internal static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const int AppModelErrorNoApplication = 15703;
    private const uint PackageFilterHead = 0x00000010;
    private const uint PackagePropertyDevelopmentMode = 0x00010000;

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

    /// <summary>
    /// How the running package was installed, or null when running unpackaged.
    /// </summary>
    /// <remarks>
    /// The family name is the same for a Store install, a locally test-signed
    /// MSIX, and a loose-layout registration of one checkout, so diagnostics
    /// need the origin and location to tell them apart. Each detail is read
    /// independently: one that Windows cannot report is left null rather than
    /// hiding the others.
    /// </remarks>
    public static PackageProvenance? TryReadProvenance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? fullName = TryGetPackageIdentity(GetCurrentPackageFullName);
        if (fullName is null)
        {
            return null;
        }

        return new PackageProvenance(
            fullName,
            TryReadOrigin(fullName),
            TryReadDevelopmentMode(),
            TryReadInstallPath());
    }

    internal static PackageOrigin? TryReadOrigin(string packageFullName) =>
        GetStagedPackageOrigin(packageFullName, out int origin) == 0 &&
        Enum.IsDefined((PackageOrigin)origin)
            ? (PackageOrigin)origin
            : null;

    internal static string? TryReadInstallPath()
    {
        try
        {
            return TryGetPackageIdentity(GetCurrentPackagePath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static bool? TryReadDevelopmentMode()
    {
        uint length = 0;
        int result = GetCurrentPackageInfo(PackageFilterHead, ref length, null, out _);
        if (result != ErrorInsufficientBuffer || length < sizeof(uint) * 2)
        {
            return null;
        }

        var buffer = new byte[length];
        result = GetCurrentPackageInfo(PackageFilterHead, ref length, buffer, out uint count);
        if (result != 0 || count == 0)
        {
            return null;
        }

        // The head PACKAGE_INFO leads the buffer: UINT32 reserved, then UINT32
        // flags. Only that value is read, so the string pointers that follow,
        // which point into this buffer, are never dereferenced.
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sizeof(uint)));
        return (flags & PackagePropertyDevelopmentMode) != 0;
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
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)]
        char[]? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackagePath(
        ref uint pathLength,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)]
        char[]? path);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageInfo(
        uint flags,
        ref uint bufferLength,
        [Out] byte[]? buffer,
        out uint count);

    // The AppModel documentation places this export in kernelbase.dll;
    // kernel32.dll does not forward it.
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode)]
    private static extern int GetStagedPackageOrigin(
        string packageFullName,
        out int origin);

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
