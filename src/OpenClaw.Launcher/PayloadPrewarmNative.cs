using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Launcher;

internal static class PayloadPrewarmNative
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint BelowNormalPriorityClass = 0x00004000;
    private const uint ProcessModeBackgroundBegin = 0x00100000;
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Starts <paramref name="executable"/> without a console, inherited
    /// handles, or a wait, and returns its process ID.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
    /// always inherits handles, which would let the worker hold a caller's
    /// redirected standard output open and delay its end-of-file. Breakaway
    /// from the caller's job is attempted so a kill-on-close job does not end
    /// the worker with setup; a job that forbids breakaway still gets a worker.
    /// </remarks>
    public static int StartDetached(string executable, string argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);

        uint flags = CreateNoWindow | CreateNewProcessGroup | BelowNormalPriorityClass;
        if (TryCreate(executable, argument, flags | CreateBreakawayFromJob, out ProcessInformation information, out int error) ||
            (error == ErrorAccessDenied &&
                TryCreate(executable, argument, flags, out information, out error)))
        {
            _ = CloseHandle(information.Thread);
            _ = CloseHandle(information.Process);
            return checked((int)information.ProcessId);
        }

        throw new Win32Exception(error);
    }

    // Background mode lowers CPU, I/O, and memory priority for the whole pass.
    // Windows lets only the process itself enter it, so the worker calls this
    // and the interactive setup process never does. Returns the Win32 error on
    // failure.
    public static int? TryEnterBackgroundMode() =>
        SetPriorityClass(GetCurrentProcess(), ProcessModeBackgroundBegin)
            ? null
            : Marshal.GetLastPInvokeError();

    // Ends only the calling worker process with the worker's success code.
    // Windows cancels its outstanding I/O; a driver that never completes a
    // request can still delay the final teardown of the process object.
    public static void TerminateCurrentProcess() =>
        _ = TerminateProcess(GetCurrentProcess(), 0);

    private static bool TryCreate(
        string executable,
        string argument,
        uint flags,
        out ProcessInformation information,
        out int error)
    {
        char[] commandLine = $"\"{executable}\" {argument}\0".ToCharArray();
        StartupInfo startupInfo = new() { Size = Marshal.SizeOf<StartupInfo>() };
        if (CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                flags,
                IntPtr.Zero,
                Environment.SystemDirectory,
                ref startupInfo,
                out information))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
