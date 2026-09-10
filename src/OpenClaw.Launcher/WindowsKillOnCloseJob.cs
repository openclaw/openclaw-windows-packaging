using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Launcher;

internal sealed class WindowsKillOnCloseJob : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private readonly SafeJobHandle _handle;

    private WindowsKillOnCloseJob(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public static WindowsKillOnCloseJob Create()
    {
        SafeJobHandle handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to create the OpenClaw process job.");
        }

        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(
                        handle,
                        JobObjectExtendedLimitInformationClass,
                        buffer,
                        (uint)size))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to configure the OpenClaw process job.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return new WindowsKillOnCloseJob(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public Process StartProcess(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute)
        {
            throw new ArgumentException(
                "Job processes cannot use shell execution.",
                nameof(startInfo));
        }
        if (startInfo.RedirectStandardInput ||
            startInfo.RedirectStandardOutput ||
            startInfo.RedirectStandardError)
        {
            throw new ArgumentException(
                "Job processes must inherit the launcher's standard streams.",
                nameof(startInfo));
        }

        string commandLine = BuildCommandLine(startInfo);

        // CreateProcessW may write to lpCommandLine, so it needs a writable,
        // null-terminated buffer rather than a marshalled immutable string.
        // The array is one element longer than the command line, and the extra
        // trailing element is already '\0'. The buffer is never read back.
        char[] commandLineBuffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, commandLineBuffer, 0, commandLine.Length);

        IntPtr environment = BuildEnvironmentBlock(startInfo);
        var startupInfo = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseStdHandles,
            StandardInput = GetStdHandle(StandardInputHandle),
            StandardOutput = GetStdHandle(StandardOutputHandle),
            StandardError = GetStdHandle(StandardErrorHandle)
        };

        try
        {
            if (!CreateProcessW(
                    startInfo.FileName,
                    commandLineBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateSuspended | CreateUnicodeEnvironment,
                    environment,
                    string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                        ? null
                        : startInfo.WorkingDirectory,
                    ref startupInfo,
                    out ProcessInformation processInformation))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to start the OpenClaw process.");
            }

            try
            {
                if (!AssignProcessToJobObject(
                        _handle,
                        processInformation.Process))
                {
                    int error = Marshal.GetLastWin32Error();
                    TerminateProcess(processInformation.Process, 1);
                    throw new Win32Exception(
                        error,
                        "Unable to assign OpenClaw to its process job.");
                }

                Process process = Process.GetProcessById(
                    unchecked((int)processInformation.ProcessId));
                _ = process.SafeHandle;
                if (ResumeThread(processInformation.Thread) == uint.MaxValue)
                {
                    int error = Marshal.GetLastWin32Error();
                    process.Dispose();
                    throw new Win32Exception(
                        error,
                        "Unable to resume the OpenClaw process.");
                }

                return process;
            }
            finally
            {
                CloseHandle(processInformation.Thread);
                CloseHandle(processInformation.Process);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    public void Dispose() => _handle.Dispose();

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var commandLine = new StringBuilder();
        AppendArgument(commandLine, startInfo.FileName);
        foreach (string argument in startInfo.ArgumentList)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, argument);
        }

        return commandLine.ToString();
    }

    private static void AppendArgument(StringBuilder commandLine, string argument)
    {
        commandLine.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                commandLine.Append('\\', (backslashes * 2) + 1);
                commandLine.Append('"');
                backslashes = 0;
                continue;
            }

            commandLine.Append('\\', backslashes);
            backslashes = 0;
            commandLine.Append(character);
        }

        commandLine.Append('\\', backslashes * 2);
        commandLine.Append('"');
    }

    private static IntPtr BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var environment = new SortedDictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry variable in
                 Environment.GetEnvironmentVariables())
        {
            environment[(string)variable.Key] = (string?)variable.Value ?? "";
        }

        foreach ((string key, string? value) in startInfo.Environment)
        {
            if (value is null)
            {
                environment.Remove(key);
            }
            else
            {
                environment[key] = value;
            }
        }

        string block = string.Join(
            '\0',
            environment.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(
        IntPtr jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeJobHandle job,
        IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
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
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
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

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
