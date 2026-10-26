using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenClaw.SessionProtocol;
using GuestCoordinator = OpenClaw.SessionHost.SessionInvocationCoordinator;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class NaturalExitJobSnapshotTests
{
    private const uint ObserverAccess = 0x00101000;

    [Fact]
    public Task NaturalExitAfterClosedAdmissionSnapshotPreservesIndependentExitEvidence() =>
        RunExperimentAsync();

    [Theory]
    [InlineData(87)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public Task SyntheticNativeOpenFailureClassifiesOnlySpecificExpiredSnapshotId(int nativeError) =>
        RunExperimentAsync(nativeError);

    [Fact]
    public Task SyntheticReusedNonmemberHandleDoesNotCountAsExitedSnapshotMember() =>
        RunExperimentAsync(nonmember: true);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidSnapshotIdCannotReachTheNativeOpenOrExpirationClassification(int processId)
    {
        using var job = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            GuestCoordinator.PinSnapshotMember(job, processId,
                (_, _, _) => throw new InvalidOperationException("Native open must not run for an invalid snapshot PID.")));
        Assert.Contains("invalid process identifier", failure.Message, StringComparison.Ordinal);
    }

    private static async Task RunExperimentAsync(int? syntheticError = null, bool nonmember = false)
    {
        string directory = TestDirectory.Create();
        string eventPrefix = @"Local\OpenClaw.PidLifetime." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + ".ready");
        using var exit = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + ".exit");
        using var witnessed = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + ".witnessed");
        using var creatorClosed = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + ".closed");
        using var finish = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + ".finish");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var elapsed = Stopwatch.StartNew();
        int Remaining() => (int)Math.Max(0, 60_000 - elapsed.ElapsedMilliseconds);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using WindowsInvocationJob lifetime = WindowsInvocationJob.Create(identity.User!.Value);
        string id = lifetime.Mode[WindowsInvocationJob.CoordinatorModePrefix.Length..];
        using SafeFileHandle job = OpenJobObjectW(0x000e, false, @"Global\OpenClaw.Invocation." + id);
        if (job.IsInvalid)
        {
            throw NativeFailure("open the fixture's exact job");
        }

        Process? helper = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        try
        {
            string parentScript = Path.Combine(directory, "parent.ps1");
            string leafScript = Path.Combine(directory, "leaf.ps1");
            string request = Path.Combine(directory, "request.json");
            File.WriteAllText(parentScript, ParentScript);
            File.WriteAllText(leafScript, LeafScript);
            File.WriteAllText(request, SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
            {
                RequestId = "natural-exit-snapshot",
                Executable = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", parentScript,
                    "-Root", directory, "-Events", eventPrefix],
                WorkingDirectory = directory,
                Environment = CancellationPrototypeProbe.FixtureEnvironment(directory, grantHostObservation: false)
            }));
            helper = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe"),
                ArgumentList = { WindowsInvocationJob.ModePrefix + id, request },
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Unable to start the natural-exit fixture.");
            stdout = helper.StandardOutput.ReadToEndAsync();
            stderr = helper.StandardError.ReadToEndAsync();
            Assert.True(ready.WaitOne(Remaining()), "The fixture member did not become ready.");

            int processId = int.Parse(File.ReadAllText(Path.Combine(directory, "leaf.pid")),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(processId > 0, "The trusted fixture PID must exclude System Idle Process.");
            using SafeProcessHandle witness = OpenProcess(ObserverAccess, false, processId);
            if (witness.IsInvalid)
            {
                throw NativeFailure("pin the independent natural-exit witness");
            }
            if (!IsProcessInJob(witness, job, out bool member))
            {
                throw NativeFailure("validate the independent witness job membership");
            }
            Assert.True(member, "The witness must identify a member of the exact fixture job.");
            ulong created = CreationTime(witness);
            Assert.Equal(258u, WaitForSingleObject(witness, 0));

            var limits = new JobLimits { LimitFlags = 0x2008, ActiveProcessLimit = 1 };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
            {
                throw NativeFailure("close fixture admission before the snapshot");
            }
            int[] snapshot = Snapshot(job);
            Assert.Contains(processId, snapshot);
            Console.WriteLine($"NATURAL_EXIT snapshot job={id} pid={processId} mask=0x{ObserverAccess:X8} " +
                $"created={created} admissionLimit=1");

            // This gate orders actual natural exit after the native snapshot
            // and before the one attempted open. No production hook is needed.
            exit.Set();
            uint exitWait = WaitForSingleObject(witness, (uint)Remaining());
            if (exitWait == uint.MaxValue)
            {
                throw NativeFailure("wait for the independent natural-exit witness");
            }
            Assert.Equal(0u, exitWait);
            if (!GetExitCodeProcess(witness, out uint exitCode))
            {
                throw NativeFailure("read the independently witnessed natural exit code");
            }
            Assert.Equal(0u, exitCode);
            witnessed.Set();
            Assert.True(creatorClosed.WaitOne(Remaining()),
                "The creator did not acknowledge disposing its member Process handle.");
            Console.WriteLine($"NATURAL_EXIT witness pid={processId} wait=0x{exitWait:X8} exit={exitCode} " +
                "creatorReferenceClosed=True");
            witness.Dispose();
            Console.WriteLine($"NATURAL_EXIT testReferenceClosed={witness.IsClosed}; releasing post-snapshot open");

            if (syntheticError is not null || nonmember)
            {
                SafeProcessHandle? supplied = null;
                int calls = 0;
                (SafeProcessHandle Handle, int Error) SyntheticOpen(uint access, bool inherit, int suppliedPid)
                {
                    calls++;
                    Assert.Equal(ObserverAccess, access);
                    Assert.False(inherit);
                    Assert.Equal(processId, suppliedPid);
                    Assert.Contains(suppliedPid, snapshot);
                    supplied = nonmember
                        ? OpenProcess(access, inherit, Environment.ProcessId)
                        : new SafeProcessHandle(IntPtr.Zero, ownsHandle: true);
                    if (nonmember && supplied.IsInvalid)
                    {
                        throw NativeFailure("open the synthetic nonmember's real process handle");
                    }
                    return (supplied, syntheticError ?? 0);
                }

                if (syntheticError == 87)
                {
                    using SafeProcessHandle? expired =
                        GuestCoordinator.PinSnapshotMember(job, processId, SyntheticOpen);
                    Assert.Null(expired);
                }
                else if (nonmember)
                {
                    InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
                        GuestCoordinator.PinSnapshotMember(job, processId, SyntheticOpen));
                    Assert.Contains("changed before it could be pinned", failure.Message, StringComparison.Ordinal);
                }
                else
                {
                    Win32Exception failure = Assert.Throws<Win32Exception>(() =>
                        GuestCoordinator.PinSnapshotMember(job, processId, SyntheticOpen));
                    Assert.Equal(syntheticError, failure.NativeErrorCode);
                    Assert.Contains("exit drain is unconfirmed", failure.Message, StringComparison.Ordinal);
                }
                Assert.Equal(1, calls);
                Assert.True(supplied!.IsClosed);
                Console.WriteLine($"SYNTHETIC_NATIVE_OPEN pid={processId} error={syntheticError} nonmember={nonmember}; " +
                    "injected classification only, not an observed OS expiration error");
            }
            else
            {
                using SafeProcessHandle? reopened = GuestCoordinator.PinSnapshotMember(
                    job, processId, (access, inherit, suppliedPid) =>
                    {
                        Assert.Equal(ObserverAccess, access);
                        Assert.False(inherit);
                        Assert.Equal(processId, suppliedPid);
                        SafeProcessHandle process = OpenProcess(access, inherit, suppliedPid);
                        int error = process.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                        Console.WriteLine($"NATURAL_EXIT open pid={suppliedPid} invalid={process.IsInvalid} error={error}");
                        return (process, error);
                    });
                if (reopened is null)
                {
                    Report("NATURAL_EXIT specific-error-87 branch observed after independently proven exit");
                }
                else
                {
                    Assert.Equal(created, CreationTime(reopened));
                    uint reopenedWait = WaitForSingleObject(reopened, 0);
                    if (reopenedWait == uint.MaxValue)
                    {
                        throw NativeFailure("wait for the naturally exited reopened member");
                    }
                    Assert.Equal(0u, reopenedWait);
                    Report("NATURAL_EXIT retained reference: valid signaled handle; error-87 branch NOT exercised");
                }
            }

            finish.Set();
            await helper.WaitForExitAsync(deadline.Token).ConfigureAwait(true);
            Assert.Equal(0, helper.ExitCode);
            Assert.Empty(await stderr.ConfigureAwait(true));
            Assert.Empty(await stdout.ConfigureAwait(true));
        }
        finally
        {
            exit.Set();
            witnessed.Set();
            finish.Set();
            await CleanupAsync(helper, job, stdout, stderr).ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Report(string message) => Console.Out.WriteLine(message);

    private static async Task CleanupAsync(
        Process? helper, SafeFileHandle job, Task<string>? stdout, Task<string>? stderr)
    {
        if (helper is null)
        {
            return;
        }
        try
        {
            if (!helper.HasExited)
            {
                if (!TerminateJobObject(job, 130))
                {
                    throw NativeFailure("terminate only the task-owned fixture job");
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await helper.WaitForExitAsync(cleanup.Token).ConfigureAwait(true);
            }
            if (stdout is not null && stderr is not null)
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(true);
            }
        }
        finally
        {
            helper.Dispose();
        }
    }

    private static int[] Snapshot(SafeFileHandle job)
    {
        const int capacity = 16;
        int size = 8 + capacity * IntPtr.Size;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, 3, buffer, (uint)size, out _))
            {
                throw NativeFailure("read the exact-job native process snapshot");
            }
            int count = Marshal.ReadInt32(buffer, 4);
            Assert.InRange(count, 1, capacity);
            Assert.Equal(Marshal.ReadInt32(buffer), count);
            return [.. Enumerable.Range(0, count)
                .Select(index => checked((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size)))];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ulong CreationTime(SafeProcessHandle process)
    {
        if (!GetProcessTimes(process, out FileTime created, out _, out _, out _))
        {
            throw NativeFailure("read the pinned process creation identity");
        }
        return ((ulong)created.High << 32) | created.Low;
    }

    private static Win32Exception NativeFailure(string operation) =>
        new(Marshal.GetLastWin32Error(), $"Unable to {operation}.");

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low, High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, IntPtr buffer, uint size, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);

    private const string ParentScript = """
        param([string]$Root, [string]$Events)
        $ErrorActionPreference = 'Stop'
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
        $start.UseShellExecute = $false
        foreach ($value in @('-NoLogo','-NoProfile','-NonInteractive','-File',
            (Join-Path $Root 'leaf.ps1'),'-Root',$Root,'-Events',$Events)) {
            $start.ArgumentList.Add($value)
        }
        $child = [Diagnostics.Process]::Start($start)
        $witnessed = [Threading.EventWaitHandle]::OpenExisting($Events + '.witnessed')
        try {
            $child.WaitForExit()
            if ($child.ExitCode -ne 0) { throw "Fixture member exited $($child.ExitCode)." }
            if (-not $witnessed.WaitOne([TimeSpan]::FromSeconds(60))) { throw 'Fixture witness gate timed out.' }
        } finally {
            $child.Dispose()
            $witnessed.Dispose()
        }
        $closed = [Threading.EventWaitHandle]::OpenExisting($Events + '.closed')
        $finish = [Threading.EventWaitHandle]::OpenExisting($Events + '.finish')
        try {
            $null = $closed.Set()
            if (-not $finish.WaitOne([TimeSpan]::FromSeconds(60))) { throw 'Fixture finish gate timed out.' }
        } finally {
            $finish.Dispose()
            $closed.Dispose()
        }
        """;

    private const string LeafScript = """
        param([string]$Root, [string]$Events)
        $ErrorActionPreference = 'Stop'
        $ready = [Threading.EventWaitHandle]::OpenExisting($Events + '.ready')
        $exit = [Threading.EventWaitHandle]::OpenExisting($Events + '.exit')
        try {
            [IO.File]::WriteAllText((Join-Path $Root 'leaf.pid'), [string]$PID)
            $null = $ready.Set()
            if (-not $exit.WaitOne([TimeSpan]::FromSeconds(60))) { throw 'Fixture natural-exit gate timed out.' }
        } finally {
            $exit.Dispose()
            $ready.Dispose()
        }
        exit 0
        """;
}
