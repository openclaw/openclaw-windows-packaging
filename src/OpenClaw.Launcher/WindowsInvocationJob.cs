using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

#if OPENCLAW_SESSION_HOST
namespace OpenClaw.SessionHost;
#else
namespace OpenClaw.Launcher;
#endif

// Prototype bridge, deliberately separate from the versioned launch JSON.
// An older helper rejects this mode instead of silently ignoring containment.
internal interface IInvocationLifetime : IDisposable
{
    string Mode { get; }
    void MarkDispatched();
    void RequestStop();
    void ForegroundExited();
    void Stop();
}

internal sealed class WindowsInvocationJob : IInvocationLifetime
{
    internal const string ModePrefix = "--request-in-job=";
    internal const string CoordinatorModePrefix = "--coordinate-invocation=";
    private const string NamePrefix = @"Global\OpenClaw.Invocation.";
    private readonly SafeFileHandle _job;
    private readonly EventWaitHandle _stopping;
    private readonly EventWaitHandle _completed;
    private readonly EventWaitHandle _pinned;
    private readonly EventWaitHandle _terminated;
    private readonly EventWaitHandle _drained;
    private readonly EventWaitHandle _foregroundExited = new(false, EventResetMode.ManualReset);
    private readonly RegisteredWaitHandle _completion;
    private readonly Lock _gate = new();
    private Exception? _terminationFailure;
    private bool _admissionClosed;
    private bool _dispatched;
    private bool _drainConfirmed;
    private bool _terminationAttempted;

    public string Mode { get; }

    private WindowsInvocationJob(
        string id, SafeFileHandle job, EventWaitHandle stopping,
        EventWaitHandle completed, EventWaitHandle pinned,
        EventWaitHandle terminated, EventWaitHandle drained)
    {
        Mode = CoordinatorModePrefix + id;
        _job = job;
        _stopping = stopping;
        _completed = completed;
        _pinned = pinned;
        _terminated = terminated;
        _drained = drained;
        _completion = ThreadPool.RegisterWaitForSingleObject(
            _completed, (_, _) => RequestStop(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public static WindowsInvocationJob Create(string agentSid)
    {
        _ = new SecurityIdentifier(agentSid);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string owner = identity.User!.Value;
        string id = Guid.NewGuid().ToString("N");
        string name = NamePrefix + id;
        using var jobSecurity = new ObjectSecurity(
            $"D:P(A;;GA;;;{owner})(A;;0x0005;;;{agentSid})");
        SafeFileHandle? job = null;
        EventWaitHandle? stopping = null;
        EventWaitHandle? completed = null;
        EventWaitHandle? pinned = null;
        EventWaitHandle? terminated = null;
        EventWaitHandle? drained = null;
        try
        {
            job = CreateJobObjectW(ref jobSecurity.Attributes, name);
            if (job.IsInvalid)
            {
                throw Failure("create the invocation job");
            }
            // Match the existing kill-on-close job's layout, without copying
            // its CreateProcess path: the helper joins before starting children.
            var limits = new ExtendedLimits { LimitFlags = 0x2000 };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                throw Failure("configure the invocation job");
            }
            stopping = CreateEvent(name + ".stopping", owner, agentSid, 0x00100000);
            completed = CreateEvent(name + ".completed", owner, agentSid, 0x00000002);
            pinned = CreateEvent(name + ".pinned", owner, agentSid, 0x00000002);
            terminated = CreateEvent(name + ".terminated", owner, agentSid, 0x00100000);
            drained = CreateEvent(name + ".drained", owner, agentSid, 0x00000002);
            var result = new WindowsInvocationJob(id, job, stopping, completed, pinned, terminated, drained);
            job = null;
            stopping = null;
            completed = null;
            pinned = null;
            terminated = null;
            drained = null;
            return result;
        }
        finally
        {
            stopping?.Dispose();
            completed?.Dispose();
            pinned?.Dispose();
            terminated?.Dispose();
            drained?.Dispose();
            job?.Dispose();
        }
    }

    internal static bool IsMode(string mode) =>
        mode.StartsWith(ModePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(mode.AsSpan(ModePrefix.Length), "N", out _);

    internal static bool IsCoordinatorMode(string mode) =>
        mode.StartsWith(CoordinatorModePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(mode.AsSpan(CoordinatorModePrefix.Length), "N", out _);

    public void MarkDispatched()
    {
        lock (_gate)
        {
            _dispatched = true;
        }
        if (_stopping.WaitOne(0) || _completed.WaitOne(0))
        {
            // Completion/cancellation can precede the native spawn return.
            // Resume the latched stop now that a coordinator is owned.
            RequestStop();
        }
    }

    public void ForegroundExited()
    {
        _foregroundExited.Set();
        Stop();
    }

    // The worker opens only ASSIGN_PROCESS and closes it before spawn.
    // The outside coordinator separately opens QUERY, never TERMINATE.
    internal static void Join(string mode)
    {
        if (!IsMode(mode))
        {
            throw new InvalidOperationException("Invalid invocation job mode.");
        }
        string name = NamePrefix + mode[ModePrefix.Length..];
        using SafeFileHandle job = OpenJobObjectW(0x0001, false, name);
        if (job.IsInvalid)
        {
            throw Failure("open the invocation job");
        }
        using Process self = Process.GetCurrentProcess();
        if (!AssignProcessToJobObject(job, self.Handle))
        {
            throw Failure("join the invocation job");
        }
        using SafeWaitHandle handle = OpenEventW(0x00100000, false, name + ".stopping");
        if (handle.IsInvalid)
        {
            throw Failure("open the invocation stop event");
        }
        using var stopping = new EventWaitHandle(false, EventResetMode.ManualReset) { SafeWaitHandle = handle };
        // A cancelled spawn can arrive after an empty job was terminated.
        // Joining first, then checking the latched event closes that race.
        if (stopping.WaitOne(0))
        {
            throw new InvalidOperationException("The invocation was stopped before its workload started.");
        }
    }

    public void RequestStop()
    {
        try
        {
            Stop();
        }
        catch (Exception exception) when (exception is Win32Exception or TimeoutException or InvalidOperationException)
        {
            // Cancellation callbacks must not throw on the caller's thread.
            // The execution owner surfaces this failure before releasing state.
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            ThrowRecordedFailure();
            try
            {
                StopCore();
            }
            catch (Exception exception) when (exception is Win32Exception or TimeoutException or InvalidOperationException)
            {
                // Preserve the first failed attempt; later cleanup paths must
                // not silently retry it or spend another termination budget.
                _terminationFailure = exception;
                _stopping.Set();
                if (!_terminationAttempted && _dispatched)
                {
                    // Admission failure cannot strand the outside coordinator.
                    // This is the first termination attempt, never a retry.
                    _terminationAttempted = true;
                    if (TerminateJobObject(_job, 130))
                    {
                        _terminated.Set();
                    }
                    else
                    {
                        Win32Exception cleanupFailure = Failure("terminate the invocation job");
                        _terminationFailure = new InvalidOperationException(
                            "Invocation admission closure and emergency termination both failed.",
                            new AggregateException(exception, cleanupFailure));
                    }
                }
                throw;
            }
        }
    }

    private void StopCore()
    {
        var elapsed = Stopwatch.StartNew();
        int Remaining() => (int)Math.Max(0, 30_000 - elapsed.ElapsedMilliseconds);
        if (!_admissionClosed)
        {
            // A finite membership snapshot is safe only after new workload
            // admission is closed. Existing members remain alive to pin.
            var limits = new ExtendedLimits { LimitFlags = 0x2008, ActiveProcessLimit = 1 };
            if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                throw Failure("close invocation process admission");
            }
            _admissionClosed = true;
        }
        _stopping.Set();
        if (!_dispatched || _drainConfirmed)
        {
            return;
        }
        int pinned = WaitHandle.WaitAny([_pinned, _foregroundExited], Remaining());
        _terminationAttempted = true;
        if (!TerminateJobObject(_job, 130))
        {
            throw Failure("terminate the invocation job");
        }
        _terminated.Set();
        if (pinned != 0)
        {
            throw new InvalidOperationException(
                "The invocation coordinator did not pin its process handles before exiting or timing out; " +
                "termination was requested but exit drain is unconfirmed.");
        }
        if (WaitHandle.WaitAny([_drained, _foregroundExited], Remaining()) != 0)
        {
            throw new InvalidOperationException(
                "The invocation coordinator did not confirm process exit before exiting or timing out.");
        }
        _drainConfirmed = true;
    }

    private void ThrowRecordedFailure()
    {
        if (_terminationFailure is { } failure)
        {
            throw new InvalidOperationException("Invocation cancellation failed.", failure);
        }
    }

    public void Dispose()
    {
        using var stopped = new EventWaitHandle(false, EventResetMode.ManualReset);
        if (_completion.Unregister(stopped))
        {
            stopped.WaitOne();
        }
        _stopping.Dispose();
        _completed.Dispose();
        _pinned.Dispose();
        _terminated.Dispose();
        _drained.Dispose();
        _foregroundExited.Dispose();
        _job.Dispose();
    }

    private static EventWaitHandle CreateEvent(string name, string owner, string agentSid, uint access)
    {
        using var security = new ObjectSecurity($"D:P(A;;GA;;;{owner})(A;;0x{access:X8};;;{agentSid})");
        SafeWaitHandle handle = CreateEventW(ref security.Attributes, true, false, name);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Unable to create the invocation lifecycle event.");
        }
        return new EventWaitHandle(false, EventResetMode.ManualReset) { SafeWaitHandle = handle };
    }

    private static Win32Exception Failure(string operation) =>
        new(Marshal.GetLastWin32Error(), $"Unable to {operation}.");

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    private sealed class ObjectSecurity : IDisposable
    {
        public SecurityAttributes Attributes;

        public ObjectSecurity(string sddl)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr descriptor, out _))
            {
                throw Failure("construct invocation object access");
            }
            Attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor
            };
        }

        public void Dispose() => LocalFree(Attributes.Descriptor);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(ref SecurityAttributes attributes, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(
        ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEventW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint size);
}
