using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

#if OPENCLAW_SESSION_HOST
namespace OpenClaw.SessionHost;
#else
namespace OpenClaw.Launcher;
#endif

internal sealed class WindowsKillOnCloseJob : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectBasicProcessIdListClass = 3;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint ProcessSynchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorMoreData = 234;
    private const int ErrorFileNotFound = 2;
    private const uint JobObjectSetAttributes = 0x0002;
    private const uint JobObjectQuery = 0x0004;
    private const uint JobObjectTerminate = 0x0008;
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private readonly SafeJobHandle _handle;
    private static readonly SearchValues<char> QuoteCharacters = SearchValues.Create(" \t\r\n\v\"");

    private readonly bool _owned;

    private WindowsKillOnCloseJob(SafeJobHandle handle, bool owned = true)
    {
        _handle = handle;
        _owned = owned;
    }

    /// <summary>The name a supervised gateway's job is published under, bound to that supervisor's identity.</summary>
    public static string GatewayJobName(int processId, long startTimeUtcTicks) =>
        $@"Global\OpenClaw.Gateway.{processId}.{startTimeUtcTicks}";

    /// <summary>
    /// Opens another process's named job for query and termination, or returns null when none exists.
    /// </summary>
    public static WindowsKillOnCloseJob? TryOpen(string name)
    {
        SafeJobHandle handle = OpenJobObjectW(JobObjectSetAttributes | JobObjectQuery | JobObjectTerminate, false, name);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == ErrorFileNotFound)
            {
                return null;
            }
            throw new Win32Exception(error, "Unable to open the OpenClaw process job.");
        }
        return new WindowsKillOnCloseJob(handle, owned: false);
    }

    public static WindowsKillOnCloseJob Create(string? name = null)
    {
        SafeJobHandle handle = CreateJobObjectW(IntPtr.Zero, name);
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

    public Process StartProcess(ProcessStartInfo startInfo) => StartProcess(startInfo, null, null, null);

    public Process StartProcess(
        ProcessStartInfo startInfo,
        SafeHandle? input,
        SafeHandle? output,
        SafeHandle? standardError,
        bool assignToJob = true)
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

        using SafeFileHandle? inputCopy = DuplicateForChild(input, StandardInputHandle);
        using SafeFileHandle? outputCopy = DuplicateForChild(output, StandardOutputHandle);
        using SafeFileHandle? errorCopy = DuplicateForChild(standardError, StandardErrorHandle);
        using var handles = new InheritedHandles(
            [inputCopy?.DangerousGetHandle() ?? 0, outputCopy?.DangerousGetHandle() ?? 0, errorCopy?.DangerousGetHandle() ?? 0]);
        IntPtr environment = BuildEnvironmentBlock(startInfo);
        var startupInfo = new ExtendedStartupInfo
        {
            Startup = new StartupInfo
            {
                Size = handles.Attributes != 0 ? Marshal.SizeOf<ExtendedStartupInfo>() : Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles,
                StandardInput = inputCopy?.DangerousGetHandle() ?? 0,
                StandardOutput = outputCopy?.DangerousGetHandle() ?? 0,
                StandardError = errorCopy?.DangerousGetHandle() ?? 0
            },
            Attributes = handles.Attributes
        };

        try
        {
            if (!CreateProcessW(
                    startInfo.FileName,
                    commandLineBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: handles.Attributes != 0,
                    CreateSuspended | CreateUnicodeEnvironment |
                    (handles.Attributes != 0 ? 0x00080000u : 0u) |
                    (startInfo.CreateNoWindow ? 0x08000000u : 0u),
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
                if (assignToJob &&
                    !AssignProcessToJobObject(
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

    private static readonly TimeSpan DisposeExitBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether disposal observed the job empty. False only if a member outlived termination.
    /// </summary>
    public bool ExitConfirmed { get; private set; } = true;

    /// <summary>
    /// Terminates every process in the job and releases it only once none remain.
    /// </summary>
    /// <remarks>
    /// Closing a kill-on-close job requests termination but does not wait for it, and a descendant that
    /// closed or redirected its streams is invisible to pipe draining. Callers that release a lease the
    /// processes depended on must check <see cref="ExitConfirmed"/> after disposal.
    /// </remarks>
    public void Dispose()
    {
        if (_handle.IsClosed)
        {
            return;
        }
        if (!_owned)
        {
            // A borrowed view of another process's job never terminates it implicitly.
            _handle.Dispose();
            return;
        }
        try
        {
            TerminateAndWait(DisposeExitBudget);
        }
        catch (Exception exception) when (exception is TimeoutException or Win32Exception)
        {
            ExitConfirmed = false;
        }
        finally
        {
            _handle.Dispose();
        }
    }

    /// <summary>The number of processes still alive in the job, descendants included.</summary>
    public int ActiveProcessCount
    {
        get
        {
            if (!QueryInformationJobObject(
                    _handle, JobObjectBasicAccountingInformationClass, out JobObjectBasicAccountingInformation information,
                    (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(), IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), "Unable to query the OpenClaw process job.");
            }
            return checked((int)information.ActiveProcesses);
        }
    }

    /// <summary>
    /// Terminates every process in the job and returns only once each member's own process handle is signaled.
    /// </summary>
    /// <remarks>
    /// Job accounting reaching zero is not proof of exit: a member can still be tearing down after it is
    /// no longer counted. Admission is closed first so a finite membership snapshot is complete, every member
    /// is pinned by handle (so a reused process ID cannot be mistaken for it), and only then is the job
    /// terminated and each pinned handle waited.
    /// </remarks>
    public void TerminateAndWait(TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        CloseAdmission();
        List<SafeProcessHandle> pinned = [];
        try
        {
            foreach (nuint processId in ReadMemberIds())
            {
                SafeProcessHandle member = OpenProcess(ProcessSynchronize | ProcessQueryLimitedInformation, false, (uint)processId);
                if (member.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    member.Dispose();
                    // Only an invalid ID identifies a member that already exited.
                    if (error == ErrorInvalidParameter)
                    {
                        continue;
                    }
                    throw new Win32Exception(error, "Unable to pin an OpenClaw process job member.");
                }
                if (!IsProcessInJob(member, _handle, out bool inJob))
                {
                    int error = Marshal.GetLastWin32Error();
                    member.Dispose();
                    throw new Win32Exception(error, "Unable to verify an OpenClaw process job member.");
                }
                if (!inJob)
                {
                    member.Dispose();
                    continue;
                }
                pinned.Add(member);
            }
            if (!TerminateJobObject(_handle, 1))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), "Unable to terminate the OpenClaw process job.");
            }
            foreach (SafeProcessHandle member in pinned)
            {
                int remaining = (int)Math.Max(0, (timeout - elapsed.Elapsed).TotalMilliseconds);
                uint wait = WaitForSingleObject(member, (uint)remaining);
                if (wait == WaitTimeout)
                {
                    throw new TimeoutException("An OpenClaw process job member did not exit after termination.");
                }
                if (wait != WaitObject0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to wait for an OpenClaw process job member.");
                }
            }
            while (ActiveProcessCount != 0)
            {
                if (elapsed.Elapsed >= timeout)
                {
                    throw new TimeoutException("The OpenClaw process job did not empty after termination.");
                }
                Thread.Sleep(5);
            }
        }
        finally
        {
            foreach (SafeProcessHandle member in pinned)
            {
                member.Dispose();
            }
        }
    }

    private void CloseAdmission()
    {
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitActiveProcess,
                ActiveProcessLimit = 1
            }
        };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), "Unable to close OpenClaw process job admission.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private List<nuint> ReadMemberIds()
    {
        int capacity = 64;
        while (true)
        {
            int size = 2 * sizeof(uint) + capacity * IntPtr.Size;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObjectBuffer(_handle, JobObjectBasicProcessIdListClass, buffer, (uint)size, IntPtr.Zero))
                {
                    int count = Marshal.ReadInt32(buffer, sizeof(uint));
                    List<nuint> ids = new(count);
                    for (int index = 0; index < count; index++)
                    {
                        ids.Add((nuint)Marshal.ReadIntPtr(buffer, 2 * sizeof(uint) + (index * IntPtr.Size)));
                    }
                    return ids;
                }
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorMoreData)
                {
                    throw new Win32Exception(error, "Unable to list OpenClaw process job members.");
                }
                capacity *= 4;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
    private static SafeFileHandle? DuplicateForChild(SafeHandle? handle, int standardHandle)
    {
        using var borrowed = new SafeFileHandle(GetStdHandle(standardHandle), ownsHandle: false);
        SafeHandle source = handle ?? borrowed;
        if (source.IsInvalid)
        {
            return null;
        }
        if (!DuplicateHandle(new IntPtr(-1), source, new IntPtr(-1), out SafeFileHandle copy, 0, true, 2))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to duplicate the child's stream handle.");
        }
        return copy;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess, SafeHandle source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedStartupInfo
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }

    private sealed class InheritedHandles : IDisposable
    {
        private IntPtr _values;
        public IntPtr Attributes { get; private set; }

        public InheritedHandles(IntPtr[] handles)
        {
            IntPtr[] valid = [.. handles.Where(handle => handle != 0 && handle != new IntPtr(-1)).Distinct()];
            if (valid.Length == 0)
            {
                return;
            }
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            if (size == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            IntPtr attributes = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                int error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(attributes);
                throw new Win32Exception(error);
            }
            Attributes = attributes;
            try
            {
                _values = Marshal.AllocHGlobal(valid.Length * IntPtr.Size);
                Marshal.Copy(valid, 0, _values, valid.Length);
                // Restrict inheritance to this launch's stdio. Otherwise a
                // concurrent child can inherit another gateway's log/pipe.
                if (!UpdateProcThreadAttribute(Attributes, 0, 0x00020002, _values,
                    (nuint)(valid.Length * IntPtr.Size), 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Attributes != 0)
            {
                DeleteProcThreadAttributeList(Attributes);
                Marshal.FreeHGlobal(Attributes);
                Attributes = 0;
            }
            Marshal.FreeHGlobal(_values);
            _values = 0;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

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
        // Match ProcessStartInfo.ArgumentList: quoting tokens that do not need
        // it can change the behavior of executables with non-CRT parsers.
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(QuoteCharacters) < 0)
        {
            commandLine.Append(argument);
            return;
        }
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
    private static extern SafeJobHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

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
    private static extern bool QueryInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        out JobObjectBasicAccountingInformation information,
        uint informationLength,
        IntPtr returnLength);

    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObjectBuffer(
        SafeJobHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        SafeProcessHandle process, SafeJobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

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
        ref ExtendedStartupInfo startupInfo,
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
