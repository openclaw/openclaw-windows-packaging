using System.Runtime.InteropServices;

#if OPENCLAW_GATEWAY_RECOVERY
namespace OpenClaw.GatewayRecovery;
#else
namespace OpenClaw.Launcher;
#endif

internal static class HostDataPaths
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static string GetProductLocalStateRoot()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        string? packageFamilyName = TryGetPackageFamilyName();
        return packageFamilyName is null
            ? Path.Combine(localAppData, "OpenClaw")
            : Path.Combine(
                localAppData,
                "Packages",
                packageFamilyName,
                "LocalState",
                "OpenClaw");
    }

    internal static string? TryGetPackageFamilyName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        uint length = 0;
        int result = GetCurrentPackageFamilyName(ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new InvalidOperationException(
                $"Unable to determine package identity (error {result}).");
        }

        var value = new char[length];
        result = GetCurrentPackageFamilyName(ref length, value);
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Unable to determine package identity (error {result}).");
        }

        return new string(value, 0, checked((int)length - 1));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(
        ref uint packageFamilyNameLength,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)]
        char[]? packageFamilyName);
}
