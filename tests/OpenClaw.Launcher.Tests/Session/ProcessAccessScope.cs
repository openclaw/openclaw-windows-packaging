using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>Restricts only a fixture child, retaining its original handle and DACL for cleanup.</summary>
internal sealed class ProcessAccessScope : IDisposable
{
    private readonly SafeProcessHandle _handle;
    private readonly IntPtr _originalDescriptor;
    private readonly IntPtr _originalDacl;

    public static void WithoutDebugPrivilege(Action action)
    {
        // ProcessManager enables SeDebugPrivilege when available. Restrict only
        // this thread's token so elevated runners cannot bypass the fixture DACL.
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!CreateRestrictedToken(identity.AccessToken, 1, 0, IntPtr.Zero,
            0, IntPtr.Zero, 0, IntPtr.Zero, out SafeAccessTokenHandle restricted))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        using (restricted)
        {
            WindowsIdentity.RunImpersonated(restricted, action);
        }
    }

    public ProcessAccessScope(Process child)
    {
        _handle = child.SafeHandle;
        uint error = GetSecurityInfo(_handle, 6, 4, out _, out _, out _originalDacl,
            out _, out _originalDescriptor);
        if (error != 0)
        {
            throw new Win32Exception((int)error);
        }
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string sddl = $"D:P(A;;0x00101000;;;{identity.User!.Value})";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr descriptor, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                if (!GetSecurityDescriptorDacl(descriptor, out _, out IntPtr dacl, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                error = SetSecurityInfo(_handle, 6, 4, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
                if (error != 0)
                {
                    throw new Win32Exception((int)error);
                }
            }
            finally
            {
                _ = LocalFree(descriptor);
            }
        }
        catch
        {
            _ = LocalFree(_originalDescriptor);
            throw;
        }
    }

    public void Dispose()
    {
        uint error = SetSecurityInfo(_handle, 6, 4, IntPtr.Zero, IntPtr.Zero, _originalDacl, IntPtr.Zero);
        _ = LocalFree(_originalDescriptor);
        if (error != 0)
        {
            throw new Win32Exception((int)error);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags,
        uint disabledSidCount, IntPtr disabledSids, uint deletedPrivilegeCount, IntPtr deletedPrivileges,
        uint restrictedSidCount, IntPtr restrictedSids, out SafeAccessTokenHandle restricted);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeProcessHandle handle, int type, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeProcessHandle handle, int type, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl,
        uint revision, out IntPtr descriptor, out uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
