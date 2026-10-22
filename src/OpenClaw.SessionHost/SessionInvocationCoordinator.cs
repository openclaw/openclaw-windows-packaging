using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

// Runs outside the workload job so host termination cannot destroy the waiter.
internal static class SessionInvocationCoordinator
{
    public static int Run(string mode, string requestPath)
    {
        string? requestId = null;
        bool stateTransfer = WindowsInvocationJob.IsStateTransferCoordinatorMode(mode);
        // Console controls reach the waiter too; only the workload should stop before pin/drain.
        ConsoleCancelEventHandler preserveWaiter = static (_, args) => args.Cancel = true;
        Console.CancelKeyPress += preserveWaiter;
        try
        {
            if (stateTransfer)
            {
                requestId = SessionStateTransferProtocol.ReadRequest(File.ReadAllText(requestPath)).RequestId;
                return Coordinate(mode, requestPath);
            }
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(File.ReadAllText(requestPath));
            requestId = request.RequestId;
            if (request.Mode != SessionLaunchMode.Attached)
            {
                throw new SessionLaunchException("Invocation coordinators support attached requests only.");
            }
            using FileStream? lease = SessionNativeStager.OpenConsumerLease(request.NativeRootPath);
            return Coordinate(mode, requestPath);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
            TimeoutException or IOException or UnauthorizedAccessException or SessionLaunchException or
            System.Text.Json.JsonException or ArgumentException)
        {
            string detail = exception is Win32Exception native
                ? $"{exception.Message} (Win32 error {native.NativeErrorCode})"
                : exception.Message;
            Console.Error.WriteLine($"openclaw-session-host: invocation drain failed: {detail}");
            if (stateTransfer)
            {
                WriteStateTransferFailure(requestPath, requestId, $"Invocation drain failed: {detail}");
            }
            else
            {
                Program.TryWriteFailure(Program.WriteResult, SessionLaunchProtocol.ResultPathFor(requestPath),
                    requestId, $"Invocation drain failed: {detail}", Console.Error);
            }
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= preserveWaiter;
        }
    }

    private static void WriteStateTransferFailure(string requestPath, string? requestId, string error)
    {
        try
        {
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionStateTransferProtocol.SerializeResult(new SessionStateTransferResult
                {
                    RequestId = requestId,
                    Error = error
                }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"openclaw-session-host: unable to write the failure result: {exception.Message}");
        }
    }

    private static int Coordinate(string mode, string requestPath)
    {
        string id = WindowsInvocationJob.InvocationId(mode);
        string workerMode = (WindowsInvocationJob.IsStateTransferCoordinatorMode(mode)
            ? WindowsInvocationJob.StateTransferModePrefix
            : WindowsInvocationJob.ModePrefix) + id;
        string name = @"Global\OpenClaw.Invocation." + id;
        using SafeFileHandle job = OpenJobObjectW(0x0004, false, name);
        if (job.IsInvalid)
        {
            throw Failure("query the invocation job");
        }
        using EventWaitHandle stopping = OpenEvent(name + ".stopping", 0x00100000);
        using EventWaitHandle completed = OpenEvent(name + ".completed", 0x00000002);
        using EventWaitHandle pinned = OpenEvent(name + ".pinned", 0x00000002);
        using EventWaitHandle terminated = OpenEvent(name + ".terminated", 0x00100000);
        using EventWaitHandle drained = OpenEvent(name + ".drained", 0x00000002);
        using Process worker = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath
                ?? throw new InvalidOperationException("The invocation coordinator cannot locate its own helper."),
            ArgumentList = { workerMode, requestPath },
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        }) ?? throw new InvalidOperationException("Unable to start the invocation worker.");
        _ = worker.SafeHandle;
        using var workerSignal = new EventWaitHandle(false, EventResetMode.ManualReset)
        {
            SafeWaitHandle = new SafeWaitHandle(worker.SafeHandle.DangerousGetHandle(), ownsHandle: false)
        };
        if (WaitHandle.WaitAny([stopping, workerSignal]) == 1)
        {
            completed.Set();
        }
        stopping.WaitOne();
        var elapsed = Stopwatch.StartNew();
        int Remaining() => (int)Math.Max(0, 30_000 - elapsed.ElapsedMilliseconds);

        var processes = new List<SafeProcessHandle>();
        try
        {
            foreach (int processId in ReadMembers(job))
            {
                if (processId == worker.Id)
                {
                    continue;
                }
                SafeProcessHandle? process = PinSnapshotMember(job, processId);
                if (process is not null)
                {
                    processes.Add(process);
                }
            }
            pinned.Set();
            if (!terminated.WaitOne(Remaining()))
            {
                throw new TimeoutException("The host did not confirm invocation termination.");
            }
            WaitForExit(worker.SafeHandle, Remaining());
            foreach (SafeProcessHandle process in processes)
            {
                WaitForExit(process, Remaining());
            }
            drained.Set();
            return worker.ExitCode;
        }
        finally
        {
            foreach (SafeProcessHandle process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal static SafeProcessHandle? PinSnapshotMember(
        SafeFileHandle job, int processId,
        Func<uint, bool, int, (SafeProcessHandle Handle, int Error)>? openProcess = null)
    {
        if (processId <= 0)
        {
            throw new InvalidOperationException("The invocation snapshot contained an invalid process identifier.");
        }
        (SafeProcessHandle process, int error) = (openProcess ?? OpenSnapshotProcess)(0x00101000, false, processId);
        try
        {
            if (process.IsInvalid)
            {
                // Only positive IDs from the exact job snapshot after admission
                // closes reach this fixed-mask open. Match .NET ProcessManager's
                // ERROR_INVALID_PARAMETER handling for an expired nonzero PID;
                // access denial and every other error still fail explicitly.
                if (error == 87)
                {
                    process.Dispose();
                    return null;
                }
                throw new Win32Exception(error,
                    $"Unable to pin invocation member {processId}; exit drain is unconfirmed.");
            }
            if (!IsProcessInJob(process, job, out bool member))
            {
                throw Failure("validate a pinned invocation member");
            }
            if (!member)
            {
                throw new InvalidOperationException(
                    $"Invocation member {processId} changed before it could be pinned.");
            }
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static (SafeProcessHandle Handle, int Error) OpenSnapshotProcess(uint access, bool inherit, int processId)
    {
        SafeProcessHandle process = OpenProcess(access, inherit, processId);
        return (process, process.IsInvalid ? Marshal.GetLastWin32Error() : 0);
    }

    private static void WaitForExit(SafeProcessHandle process, int milliseconds)
    {
        uint status = WaitForSingleObject(process, (uint)milliseconds);
        if (status == uint.MaxValue)
        {
            throw Failure("wait for a pinned invocation process");
        }
        if (status != 0)
        {
            throw new TimeoutException("A pinned invocation process did not finish exiting.");
        }
    }

    private static int[] ReadMembers(SafeFileHandle job)
    {
        int capacity = 64;
        while (true)
        {
            int size = checked(8 + capacity * IntPtr.Size);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                bool read = QueryInformationJobObject(job, 3, buffer, (uint)size, out _);
                int error = Marshal.GetLastWin32Error();
                if (!read)
                {
                    if (error == 234)
                    {
                        // Admission is closed; resize the snapshot buffer, never
                        // treat a truncated membership list as a complete drain.
                        capacity = Math.Max(capacity * 2, Marshal.ReadInt32(buffer));
                        continue;
                    }
                    throw new Win32Exception(error, "Unable to enumerate invocation job members.");
                }
                int count = Marshal.ReadInt32(buffer, 4);
                var ids = new int[count];
                for (int index = 0; index < count; index++)
                {
                    ids[index] = checked((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size));
                }
                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static EventWaitHandle OpenEvent(string name, uint access)
    {
        SafeWaitHandle handle = OpenEventW(access, false, name);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Unable to open the invocation lifecycle event.");
        }
        return new EventWaitHandle(false, EventResetMode.ManualReset) { SafeWaitHandle = handle };
    }

    private static Win32Exception Failure(string operation) =>
        new(Marshal.GetLastWin32Error(), $"Unable to {operation}.");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEventW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job, int kind, IntPtr buffer, uint size, out uint returned);
}
