using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Launcher.Gateway;

internal static class WindowsLogonSession
{
    private const uint TokenQuery = 0x0008;
    private const int TokenStatisticsClass = 10;

    public static string GetCurrentId()
    {
        if (!OpenProcessToken(
            System.Diagnostics.Process.GetCurrentProcess().SafeHandle,
            TokenQuery,
            out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (token)
        {
            int size = Marshal.SizeOf<TokenStatistics>();
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(
                    token,
                    TokenStatisticsClass,
                    buffer,
                    size,
                    out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                TokenStatistics statistics =
                    Marshal.PtrToStructure<TokenStatistics>(buffer);
                return $"{statistics.AuthenticationId.HighPart:X8}:" +
                    $"{statistics.AuthenticationId.LowPart:X8}";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId;
        public Luid AuthenticationId;
        public long ExpirationTime;
        public uint TokenType;
        public uint ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public Luid ModifiedId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
