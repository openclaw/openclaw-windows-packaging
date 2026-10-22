using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.SessionHost;

/// <summary>Queries a pinned process identity without requesting termination or memory access.</summary>
internal static class SessionProcessQuery
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    public static SafeProcessHandle Open(int processId)
    {
        SafeProcessHandle handle = OpenProcess(QueryLimitedInformation | Synchronize, false, processId);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    public static bool HasExited(SafeProcessHandle handle) => WaitForSingleObject(handle, 0) switch
    {
        0 => true,
        258 => false,
        _ => throw new Win32Exception(Marshal.GetLastWin32Error())
    };

    public static DateTimeOffset StartTime(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out long created, out _, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return DateTimeOffset.FromFileTime(created);
    }

    public static string ImagePath(SafeProcessHandle handle)
    {
        char[] path = new char[32768];
        int size = path.Length;
        if (!QueryFullProcessImageNameW(handle, 0, path, ref size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new string(path, 0, size);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, [Out] char[] path, ref int size);
}
