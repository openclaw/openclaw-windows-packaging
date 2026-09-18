using System.Runtime.InteropServices;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>Resolves paths owned by the isolated session's agent account.</summary>
internal static class AgentProfile
{
    private static readonly Guid ProfileFolderId =
        new("5E6C858F-0E22-4760-9AFE-EA3317B67173");

    public static string GetPath()
    {
        int result = SHGetKnownFolderPath(
            ProfileFolderId,
            flags: 0,
            token: IntPtr.Zero,
            out IntPtr path);
        if (result < 0)
        {
            throw new SessionLaunchException(
                $"Windows could not resolve the user profile directory " +
                $"(HRESULT 0x{result:X8}).");
        }

        try
        {
            return Marshal.PtrToStringUni(path)
                ?? throw new SessionLaunchException(
                    "Windows returned an empty user profile directory.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        in Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
