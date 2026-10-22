using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using Sdk = Microsoft.Mxc.Sdk.V1;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class InvocationCancellationTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    public Task RetainedJobHandleDoesNotPreventDescendantCleanupOrStopSibling(
        bool captured, bool normalExit, bool timeout, bool race) =>
        CancellationPrototypeProbe.RunLocalAsync(captured, normalExit, timeout, race);

    [Fact]
    public Task LoweringAdmissionLimitPreservesExistingMembersAndRefusesAnotherChild() =>
        CancellationPrototypeProbe.RunLocalAsync(false, true, admissionProbe: true);

    [Fact]
    public Task CancellationDuringNativeSpawnCannotStartLateWork() =>
        CancellationPrototypeProbe.RunLateSpawnAsync();

    [Fact]
    public Task CompletionBeforeNativeSpawnReturnsStillDrainsTheInvocation() =>
        CancellationPrototypeProbe.RunLateSpawnAsync(completedBeforeReturn: true);

    [Fact]
    public Task AgentSideWitnessPinsProcessesWithoutHostObservationGrants() =>
        CancellationPrototypeProbe.RunLocalAsync(false, false, naturalRights: true);

    [Fact]
    public async Task CancellationBeforeGuestAdmissionCannotStartLateWork()
    {
        string root = TestDirectory.Create();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using WindowsInvocationJob job = WindowsInvocationJob.Create(identity.User!.Value);
        try
        {
            job.Stop();
            string marker = Path.Combine(root, "unexpected.txt");
            string request = Path.Combine(root, "request.json");
            File.WriteAllText(request, OpenClaw.SessionProtocol.SessionLaunchProtocol.SerializeRequest(
                new OpenClaw.SessionProtocol.SessionLaunchRequest
                {
                    RequestId = "late-admission",
                    Executable = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = ["/d", "/c", $"echo late > \"{marker}\""],
                    WorkingDirectory = root
                }));
            using Process helper = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe"),
                ArgumentList = {
                    WindowsInvocationJob.ModePrefix + job.Mode[WindowsInvocationJob.CoordinatorModePrefix.Length..],
                    request },
                UseShellExecute = false,
                RedirectStandardError = true
            })!;
            string error = await helper.StandardError.ReadToEndAsync().ConfigureAwait(true);
            await helper.WaitForExitAsync().ConfigureAwait(true);
            Assert.Equal(64, helper.ExitCode);
            Assert.Contains("stopped before", error, StringComparison.Ordinal);
            Assert.False(File.Exists(marker));
            job.Stop();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

// Also callable explicitly by the disposable-session proof script. None of the
// xUnit entrypoints provision a session or touch installed/package state.
public static class CancellationPrototypeProbe
{
    private const string InvocationJobNamePrefix = @"Global\OpenClaw.Invocation.";

    public static async Task RunLateSpawnAsync(bool completedBeforeReturn = false)
    {
        string root = TestDirectory.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Task<int>? execution = null;
        try
        {
            var client = new MxcSdkSessionClient(
                new MxcRuntimeLocation(root, null),
                new LocalLifecycle(completedBeforeReturn ? null : () =>
                {
                    entered.SetResult();
                    release.Task.GetAwaiter().GetResult();
                }, completedBeforeReturn ? command =>
                {
                    int index = command.IndexOf(WindowsInvocationJob.CoordinatorModePrefix, StringComparison.Ordinal);
                    string id = command.Substring(index + WindowsInvocationJob.CoordinatorModePrefix.Length, 32);
                    using EventWaitHandle completed = EventWaitHandle.OpenExisting(InvocationJobNamePrefix + id + ".completed");
                    Assert.True(completed.WaitOne(TimeSpan.FromSeconds(60)),
                        "The fixture root did not exit before the native spawn return was released.");
                }
            : null), new QuietConsole(), new QuietStdio());
            var executor = new SessionExecutor(client, Console.WriteLine);
            string marker = Path.Combine(root, "late-work.txt");
            execution = executor.ExecuteCommandAsync(new SessionRecord
            {
                SandboxId = "iso:local-late-spawn",
                AgentUserSid = identity.User!.Value,
                WorkspacePath = root,
                Generation = Guid.NewGuid().ToString("N")
            }, new SessionCommandRequest(
                Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe"),
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                completedBeforeReturn ? ["/d", "/c", "exit", "37"] :
                    ["/d", "/c", "echo late > \"%OPENCLAW_FIXTURE_LATE_MARKER%\""],
                root)
            {
                AdditionalEnvironment = new Dictionary<string, string> { ["OPENCLAW_FIXTURE_LATE_MARKER"] = marker }
            }, "Late spawn", "Late fixture", cancellation.Token);
            if (completedBeforeReturn)
            {
                Assert.Equal(37, await execution.WaitAsync(deadline.Token).ConfigureAwait(false));
            }
            else
            {
                await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                await cancellation.CancelAsync().ConfigureAwait(false);
                Assert.False(execution.IsCompleted);
                release.SetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    execution.WaitAsync(deadline.Token)).ConfigureAwait(false);
            }
            Assert.False(File.Exists(marker));
        }
        finally
        {
            release.TrySetResult();
            if (execution is not null)
            {
                try
                { await execution.WaitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            Directory.Delete(root, true);
        }
    }

    public static Task RunDisposableAsync(
        string nativeDirectory, string helperPath, string evidenceDirectory) =>
        RunDisposableCoreAsync(nativeDirectory, helperPath, evidenceDirectory, naturalRights: false);

    public static Task RunNaturalRightsDisposableAsync(
        string nativeDirectory, string helperPath, string evidenceDirectory) =>
        RunDisposableCoreAsync(nativeDirectory, helperPath, evidenceDirectory, naturalRights: true);

    private static async Task RunDisposableCoreAsync(
        string nativeDirectory, string helperPath, string evidenceDirectory, bool naturalRights)
    {
        Directory.CreateDirectory(evidenceDirectory);
        void Report(string message)
        {
            File.AppendAllText(Path.Combine(evidenceDirectory, "raw.log"), message + "\n");
            Console.WriteLine(message);
        }
        Action<string> report = Report;
        MxcRuntimeLocation runtime = MxcRuntimeLocator.Locate(nativeDirectory);
        var client = new MxcSdkSessionClient(
            runtime, console: new QuietConsole(), stdio: new QuietStdio(), log: report);
        // Unpackaged probe: no fabricated PFN or production application identity.
        Sdk.ProvisionResult issued = Sdk.MxcLifecycle.ProvisionContainer(
            new Sdk.IsolationSessionProvisionRequest(new Sdk.NetworkPolicy
            {
                Egress = new Sdk.NetworkEgressPolicy { Default = Sdk.NetworkAction.Allow },
                Ingress = new Sdk.NetworkIngressPolicy
                {
                    Default = Sdk.NetworkAction.Allow,
                    HostLoopback = Sdk.NetworkAction.Allow
                }
            }));
        // Persist the exact issued ID and SID before start, staging, or probes.
        try
        {
            var metadata = issued.Metadata as Sdk.IsolationSessionProvisionMetadata;
            File.WriteAllText(Path.Combine(evidenceDirectory, "identity.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ContainerId = issued.ContainerId.Value,
                    metadata?.AgentUserSid,
                    metadata?.AgentUserName,
                    Workspace = metadata?.EphemeralWorkspacePath
                }));
            MxcSandboxId id = MxcSandboxId.Parse(issued.ContainerId.Value);
            Sdk.IsolationSessionProvisionMetadata session = metadata
                ?? throw new InvalidOperationException("The SDK did not return disposable-session metadata.");
            report($"PROVISION id={issued.ContainerId.Value} sid={session.AgentUserSid}");
            report($"SDK={typeof(Sdk.MxcLifecycle).Assembly.GetName().Version} " +
                $"native={Sdk.MxcPlatform.NativeVersion} OS={Environment.OSVersion.VersionString}");
            await client.StartAsync(id, CancellationToken.None).ConfigureAwait(false);
            string helper = Path.Combine(session.EphemeralWorkspacePath, "prototype-helper.exe");
            File.Copy(helperPath, helper);
            if (!naturalRights)
            {
                await RunUncontainedBaselineAsync(
                    id, session.EphemeralWorkspacePath, helper, report).ConfigureAwait(false);
            }
            else
            {
                report("NATURAL RIGHTS: no process DACL observation grants; independent same-agent pinned-handle witness. " +
                    "Legacy baseline is retained in earlier receipts, not rerun in this lane.");
            }
            await RunScenarioAsync(
                client, id.Value, session.AgentUserSid,
                session.EphemeralWorkspacePath, helper, false, false, report, naturalRights: naturalRights).ConfigureAwait(false);
            await RunScenarioAsync(
                client, id.Value, session.AgentUserSid,
                session.EphemeralWorkspacePath, helper, true, false, report, naturalRights: naturalRights).ConfigureAwait(false);
            await RunScenarioAsync(
                client, id.Value, session.AgentUserSid,
                session.EphemeralWorkspacePath, helper, false, true, report, naturalRights: naturalRights).ConfigureAwait(false);
            await RunScenarioAsync(
                client, id.Value, session.AgentUserSid,
                session.EphemeralWorkspacePath, helper, true, false, report, timeout: true,
                naturalRights: naturalRights).ConfigureAwait(false);
            await RunScenarioAsync(
                client, id.Value, session.AgentUserSid,
                session.EphemeralWorkspacePath, helper, false, true, report, race: true,
                naturalRights: naturalRights).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (issued.Metadata is Sdk.IsolationSessionProvisionMetadata owned)
                {
                    foreach (string environment in Directory.GetFiles(
                        owned.EphemeralWorkspacePath, "agent-environment.json", SearchOption.AllDirectories))
                    {
                        string directory = Path.GetFileName(Path.GetDirectoryName(environment))!;
                        File.Copy(environment, Path.Combine(evidenceDirectory, directory + "-environment.json"));
                    }
                    foreach (string result in Directory.GetFiles(owned.EphemeralWorkspacePath, "launch-*.result.json"))
                    {
                        File.Copy(result, Path.Combine(evidenceDirectory, Path.GetFileName(result)));
                    }
                }
            }
            finally
            {
                // Logging/receipt failures cannot prevent cleanup of the exact
                // issued ID. A failed stop still prevents further mutations.
                Sdk.MxcLifecycle.StopContainer(issued.ContainerId);
                Sdk.MxcLifecycle.DeprovisionContainer(issued.ContainerId);
                report($"CLEANUP stop+deprovision complete exact id={issued.ContainerId.Value}; " +
                    "independently verify account, profile, and workspace removal.");
            }
        }
    }

    private static async Task RunUncontainedBaselineAsync(
        MxcSandboxId id, string workspace, string helper, Action<string> report)
    {
        string root = Path.Combine(workspace, "baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "tree.ps1");
        File.WriteAllText(script, TreeScript);
        string requestPath = Path.Combine(root, "launch.json");
        File.WriteAllText(requestPath, OpenClaw.SessionProtocol.SessionLaunchProtocol.SerializeRequest(
            new OpenClaw.SessionProtocol.SessionLaunchRequest
            {
                RequestId = "uncontained-baseline",
                Executable = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                Arguments = ["-NoProfile", "-NonInteractive", "-File", script,
                    "-Root", root, "-Role", "parent", "-EarlyExit", "no"],
                WorkingDirectory = workspace,
                Environment = FixtureEnvironment(root)
            }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var ready = new FileArrival(root, "ready");
        using Sdk.MxcProcess process = Sdk.MxcLifecycle.SpawnInContainer(
            new Sdk.ContainerId(id.Value),
            new Sdk.ExecutionRequest(SessionExecutor.BuildGuestCommandLine(helper, requestPath)));
        process.StandardInput?.Dispose();
        Task stdout = process.StandardOutput?.CopyToAsync(Stream.Null, deadline.Token) ?? Task.CompletedTask;
        Task stderr = process.StandardError?.CopyToAsync(Stream.Null, deadline.Token) ?? Task.CompletedTask;
        using var witnesses = new ProcessHandles(report, "baseline");
        try
        {
            await ready.WaitAsync(deadline.Token).ConfigureAwait(false);
            int[] ids = ReadProcessIds(root);
            witnesses.Capture(ids);
            witnesses.AssertRunning("before-sdk-kill");
            process.Kill();
            Sdk.WaitResult result = await process.WaitAsync(deadline.Token).ConfigureAwait(false);
            witnesses.AssertRunning("after-sdk-kill");
            report($"BASELINE SDK Kill + Wait exit={result.ExitCode}; helper/workload/child/descendant " +
                $"STILL ALIVE ids={string.Join(',', ids)}. Legacy --request path, no job.");
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, "release"), "");
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        witnesses.AssertExited("after-fixture-release");
        Assert.True(File.Exists(Path.Combine(root, "late-effect")));
        report("BASELINE fixture release completed; all leaked fixture processes gone.");
    }

    public static async Task RunLocalAsync(
        bool captured, bool normalExit, bool timeout = false, bool race = false, bool admissionProbe = false,
        bool naturalRights = false)
    {
        string root = TestDirectory.Create();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        try
        {
            var client = new MxcSdkSessionClient(
                new MxcRuntimeLocation(root, null),
                new LocalLifecycle(), new QuietConsole(), new QuietStdio());
            await RunScenarioAsync(
                client, "iso:local-fixture", identity.User!.Value, root,
                Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe"),
                captured, normalExit, Console.WriteLine, timeout, race, admissionProbe, naturalRights).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    public static async Task RunScenarioAsync(
        object sessionClient, string containerId, string agentSid, string workspace,
        string helperPath, bool captured, bool normalExit, Action<string> report,
        bool timeout = false, bool race = false, bool admissionProbe = false, bool naturalRights = false)
    {
        var client = (IMxcSessionClient)sessionClient;
        var record = new SessionRecord
        {
            SandboxId = containerId,
            AgentUserSid = agentSid,
            WorkspacePath = workspace,
            Generation = Guid.NewGuid().ToString("N")
        };
        string first = Path.Combine(workspace, "first-" + record.Generation);
        string sibling = Path.Combine(workspace, "sibling-" + record.Generation);
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(sibling);
        string shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe");
        string script = Path.Combine(workspace, "tree-" + record.Generation + ".ps1");
        File.WriteAllText(script, TreeScript);
        var clock = new DeadlineClock();
        using var cancellation = timeout
            ? new CancellationTokenSource(TimeSpan.FromSeconds(1), clock)
            : new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var executor = new SessionExecutor(client, report);
        SessionCommandRequest Request(string directory, bool exitEarly) =>
            new(helperPath, shell,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script,
                    "-Root", directory, "-Role", "parent", "-EarlyExit", exitEarly ? "yes" : "no"],
                workspace)
            {
                AdditionalEnvironment = FixtureEnvironment(
                    directory, admissionProbe && directory == first, grantHostObservation: !naturalRights)
            };
        Task<int>? siblingRun = null;
        Task? firstRun = null;
        AgentProcessWitness? witness = null;
        try
        {
            using var siblingReady = new FileArrival(sibling, "ready");
            siblingRun = executor.ExecuteCommandAsync(
                record, Request(sibling, false), "Sibling start", "Sibling", CancellationToken.None);
            await siblingReady.WaitAsync(deadline.Token).ConfigureAwait(false);

            using var firstReady = new FileArrival(first, "ready");
            firstRun = captured
                ? executor.ExecuteCommandCaptureAsync(
                    record, Request(first, normalExit), "First start", "First", cancellation.Token)
                : executor.ExecuteCommandAsync(
                    record, Request(first, normalExit), "First start", "First", cancellation.Token);
            await firstReady.WaitAsync(deadline.Token).ConfigureAwait(false);
            int[] firstIds = ReadProcessIds(first);
            int[] siblingIds = ReadProcessIds(sibling);
            using var handles = new ProcessHandles(report, "first");
            using var siblingHandles = new ProcessHandles(report, "sibling");
            using var coordinator = new ProcessHandles(report, "coordinator");
            using var siblingCoordinator = new ProcessHandles(report, "sibling-coordinator");
            Assert.Equal(agentSid, File.ReadAllText(Path.Combine(first, "identity.txt")));
            Assert.Equal(agentSid, File.ReadAllText(Path.Combine(sibling, "identity.txt")));
            witness = naturalRights
                ? await AgentProcessWitness.StartAsync(client, record, helperPath, first, sibling, report, deadline.Token)
                    .ConfigureAwait(false)
                : null;
            if (witness is null)
            {
                handles.Capture(firstIds);
                siblingHandles.Capture(siblingIds);
                coordinator.Capture([int.Parse(File.ReadAllText(Path.Combine(first, "coordinator-process.txt")),
                    System.Globalization.CultureInfo.InvariantCulture)], expectedCount: 1);
                siblingCoordinator.Capture([int.Parse(File.ReadAllText(Path.Combine(sibling, "coordinator-process.txt")),
                    System.Globalization.CultureInfo.InvariantCulture)], expectedCount: 1);
                handles.AssertInInvocationJob(first);
                siblingHandles.AssertInInvocationJob(sibling);
                coordinator.AssertInInvocationJob(first, expectedMembership: false);
                siblingCoordinator.AssertInInvocationJob(sibling, expectedMembership: false);
                handles.AssertRunning("before-cancel-or-exit");
                siblingHandles.AssertRunning("before-cancel-or-exit");
                coordinator.AssertRunning("before-cancel-or-exit");
                siblingCoordinator.AssertRunning("before-cancel-or-exit");
            }
            else
            {
                foreach (string directory in new[] { first, sibling })
                {
                    using System.Text.Json.JsonDocument environment = System.Text.Json.JsonDocument.Parse(
                        File.ReadAllText(Path.Combine(directory, "agent-environment.json")));
                    Assert.False(environment.RootElement.GetProperty("HostObservationGranted").GetBoolean());
                }
            }
            if (admissionProbe)
            {
                CloseAdmission(first, report);
                handles.AssertRunning("after-admission-close");
                using var attempt = new FileArrival(first, "admission-ready");
                File.WriteAllText(Path.Combine(first, "attempt-spawn"), "");
                await attempt.WaitAsync(deadline.Token).ConfigureAwait(false);
                string outcome = File.ReadAllText(Path.Combine(first, "admission-result.txt"));
                report($"ADMISSION attempt={outcome}");
                Assert.StartsWith("REFUSED ", outcome, StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(first, "admitted-effect")));
                handles.AssertRunning("after-refused-spawn");
                siblingHandles.AssertRunning("after-refused-spawn");
                report("PASS admission-only probe; cancellation/drain was not exercised.");
                return;
            }
            report("AGENT " + File.ReadAllText(Path.Combine(first, "agent-environment.json")));
            report($"READY first={string.Join(',', firstIds)} sibling={string.Join(',', siblingIds)} " +
                $"captured={captured} normalExit={normalExit} timeout={timeout} race={race}");
            if (normalExit && !race)
            {
                File.WriteAllText(Path.Combine(first, "exit-parent"), "");
                await firstRun.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            else
            {
                if (timeout)
                {
                    clock.Fire();
                    Assert.True(cancellation.IsCancellationRequested);
                }
                else if (race)
                {
                    await Task.WhenAll(
                        Task.Run(() => File.WriteAllText(Path.Combine(first, "exit-parent"), "")),
                        Task.Run(cancellation.CancelAsync)).ConfigureAwait(false);
                }
                else
                {
                    await cancellation.CancelAsync().ConfigureAwait(false);
                }
                try
                {
                    await firstRun.WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (!race)
                    { throw new InvalidOperationException("Cancellation returned success."); }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested && firstRun.IsCompleted)
                {
                }
            }
            if (witness is null)
            {
                handles.AssertExited("foreground-return");
                coordinator.AssertExited("foreground-return");
                siblingHandles.AssertRunning("after-first-return");
                siblingCoordinator.AssertRunning("after-first-return");
            }
            else
            {
                await witness.CheckFirstAsync().ConfigureAwait(false);
            }
            File.WriteAllText(Path.Combine(first, "release"), "");
            Assert.False(File.Exists(Path.Combine(first, "late-effect")));
            File.WriteAllText(Path.Combine(sibling, "release"), "");
            Assert.Equal(0, await siblingRun.WaitAsync(deadline.Token).ConfigureAwait(false));
            if (witness is null)
            {
                siblingHandles.AssertExited("sibling-return");
                siblingCoordinator.AssertExited("sibling-return");
            }
            else
            {
                await witness.CheckSiblingAsync().ConfigureAwait(false);
            }
            report("PASS helper/workload/child/descendant gone; no late effect; sibling survived and exited normally.");
        }
        finally
        {
            // Release only fixture-owned trees, including the baseline's leaks.
            File.WriteAllText(Path.Combine(first, "release"), "");
            File.WriteAllText(Path.Combine(first, "exit-parent"), "");
            File.WriteAllText(Path.Combine(sibling, "release"), "");
            try
            {
                if (firstRun is not null)
                {
                    try
                    { await firstRun.WaitAsync(deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }
                if (siblingRun is not null)
                {
                    await siblingRun.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                if (witness is not null)
                {
                    await witness.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static int[] ReadProcessIds(string directory) =>
        [.. Directory.GetFiles(directory, "*.pid").Select(path =>
            int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture))];

    internal static Dictionary<string, string> FixtureEnvironment(
        string directory, bool admissionProbe = false, bool grantHostObservation = true)
    {
        string temporary = Path.Combine(directory, "temp");
        Directory.CreateDirectory(temporary);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new Dictionary<string, string>
        {
            ["TEMP"] = temporary,
            ["TMP"] = temporary,
            ["PSModuleAnalysisCachePath"] = Path.Combine(temporary, "module-cache"),
            ["POWERSHELL_TELEMETRY_OPTOUT"] = "1",
            ["POWERSHELL_UPDATECHECK"] = "Off",
            ["OPENCLAW_FIXTURE_OBSERVER_SID"] = grantHostObservation ? identity.User!.Value : "",
            ["OPENCLAW_FIXTURE_ADMISSION_PROBE"] = admissionProbe ? "1" : "0"
        };
    }

    private static void CloseAdmission(string directory, Action<string> report)
    {
        string mode = File.ReadAllText(Path.Combine(directory, "invocation-mode.txt")).Trim();
        Assert.True(WindowsInvocationJob.IsMode(mode));
        string name = InvocationJobNamePrefix + mode[WindowsInvocationJob.ModePrefix.Length..];
        using SafeFileHandle job = OpenJobObjectW(0x0006, false, name);
        if (job.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        var limits = new AdmissionLimits { LimitFlags = 0x2008, ActiveProcessLimit = 1 };
        uint size = (uint)Marshal.SizeOf<AdmissionLimits>();
        if (!SetInformationJobObject(job, 9, ref limits, size))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(), "Lowering active-process limit below current membership failed.");
        }
        var observed = new AdmissionLimits();
        if (!QueryInformationJobObject(job, 9, ref observed, size, out _))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        Assert.Equal(0x2008u, observed.LimitFlags);
        Assert.Equal(1u, observed.ActiveProcessLimit);
        report($"ADMISSION job={name} flags=0x{observed.LimitFlags:X8} limit={observed.ActiveProcessLimit}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdmissionLimits
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int kind, ref AdmissionLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job, int kind, ref AdmissionLimits limits, uint size, out uint returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenJobObjectW(
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    private sealed class ProcessHandles(Action<string> report, string subject) : IDisposable
    {
        private readonly List<(int Id, SafeProcessHandle Handle)> _processes = [];
        public void Capture(int[] ids, int expectedCount = 4)
        {
            Assert.Equal(expectedCount, ids.Length);
            Assert.Equal(expectedCount, ids.Distinct().Count());
            foreach (int id in ids)
            {
                SafeProcessHandle handle = OpenProcess(0x00101000, false, id);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new System.ComponentModel.Win32Exception(
                        error, $"Unable to pin {subject} fixture process {id}.");
                }
                _processes.Add((id, handle));
                report($"PIN subject={subject} pid={id} access=0x00101000");
            }
        }
        public void AssertInInvocationJob(string directory, bool expectedMembership = true)
        {
            string modePath = Path.Combine(directory, "invocation-mode.txt");
            Assert.True(File.Exists(modePath),
                $"The {subject} helper did not report its exact invocation job mode.");
            string mode = File.ReadAllText(modePath).Trim();
            Assert.True(WindowsInvocationJob.IsMode(mode),
                $"The {subject} helper reported an invalid invocation job mode.");
            string jobName = InvocationJobNamePrefix + mode[WindowsInvocationJob.ModePrefix.Length..];
            using SafeFileHandle job = OpenJobObjectW(0x0004, false, jobName);
            if (job.IsInvalid)
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), $"Unable to query exact {subject} invocation job {jobName}.");
            }
            Assert.All(_processes, process =>
            {
                if (!IsProcessInJob(process.Handle, job, out bool member))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Job membership query failed for {subject} fixture process {process.Id}.");
                }
                report($"MEMBERSHIP subject={subject} pid={process.Id} job={jobName} member={member}");
                Assert.True(member == expectedMembership,
                    $"{subject} fixture process {process.Id} has unexpected membership in exact invocation job {jobName}; " +
                    "membership failed; exit-drain was not evaluated.");
            });
        }
        public void Dispose()
        {
            foreach ((_, SafeProcessHandle handle) in _processes)
            { handle.Dispose(); }
        }
        public void AssertExited(string phase) =>
            Assert.All(_processes, process =>
            {
                uint status = ObserveWait(process.Id, process.Handle, phase);
                Assert.True(status == 0,
                    $"{subject} fixture process {process.Id} has not exited; wait status 0x{status:X8}.");
            });
        public void AssertRunning(string phase) =>
            Assert.All(_processes, process =>
            {
                uint status = ObserveWait(process.Id, process.Handle, phase);
                Assert.True(status == 258,
                    $"{subject} fixture process {process.Id} is not running; wait status 0x{status:X8}.");
            });

        private uint ObserveWait(int id, SafeProcessHandle process, string phase)
        {
            uint status = WaitForSingleObject(process, 0);
            int error = status == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
            report($"WAIT subject={subject} phase={phase} pid={id} status=0x{status:X8} error={error}");
            return status == uint.MaxValue
                ? throw new System.ComponentModel.Win32Exception(
                    error, $"Native wait failed for {subject} fixture process {id}; exit lag was not established.")
                : status;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessInJob(
            SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeProcessHandle OpenProcess(
            uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new DeadlineTimer();
        }
        public void Fire() => _callback!(_state);
        private sealed class DeadlineTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class FileArrival : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly string _path;
        private readonly TaskCompletionSource _arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileArrival(string directory, string name)
        {
            _path = Path.Combine(directory, name);
            _watcher = new FileSystemWatcher(directory, name);
            _watcher.Created += (_, _) => _arrival.TrySetResult();
            _watcher.Renamed += (_, _) => _arrival.TrySetResult();
            _watcher.Error += (_, error) => _arrival.TrySetException(error.GetException());
            _watcher.EnableRaisingEvents = true;
        }
        public Task WaitAsync(CancellationToken cancellationToken) =>
            File.Exists(_path) ? Task.CompletedTask : _arrival.Task.WaitAsync(cancellationToken);
        public bool IsPresent => File.Exists(_path);
        public void Dispose() => _watcher.Dispose();
    }

    private sealed class LocalLifecycle(
        Action? beforeSpawn = null, Action<string>? beforeSpawnReturns = null) : IMxcSdkLifecycle
    {
        public Sdk.ProvisionResult Provision(Sdk.IsolationSessionProvisionRequest request) => throw new NotSupportedException();
        public Sdk.LifecycleResult Start(Sdk.ContainerId id) => throw new NotSupportedException();
        public Sdk.LifecycleResult Stop(Sdk.ContainerId id) => throw new NotSupportedException();
        public Sdk.LifecycleResult Deprovision(Sdk.ContainerId id) => throw new NotSupportedException();
        public Sdk.IMxcProcess Spawn(Sdk.ContainerId id, Sdk.ExecutionRequest request)
        {
            beforeSpawn?.Invoke();
            var process = new LocalProcess(request.Command);
            try
            {
                beforeSpawnReturns?.Invoke(request.Command);
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
        public MxcTerminalProcess SpawnTerminal(Sdk.ContainerId id, Sdk.ExecutionRequest request, Sdk.MxcPtySize size) =>
            throw new NotSupportedException();
    }

    private sealed class LocalProcess : Sdk.IMxcProcess
    {
        private readonly Process _process;
        private readonly SafeFileHandle? _retainedJob;
        public LocalProcess(string command)
        {
            string prefix = command.Contains(WindowsInvocationJob.CoordinatorModePrefix, StringComparison.Ordinal)
                ? WindowsInvocationJob.CoordinatorModePrefix : WindowsInvocationJob.ModePrefix;
            int index = command.IndexOf(prefix, StringComparison.Ordinal);
            if (index >= 0)
            {
                string id = command.Substring(index + prefix.Length, 32);
                _retainedJob = OpenJobObjectW(0x0001, false, InvocationJobNamePrefix + id);
                Assert.False(_retainedJob.IsInvalid);
            }
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c " + command,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
        }
        public uint Id => (uint)_process.Id;
        public Stream? StandardInput => _process.StandardInput.BaseStream;
        public Stream? StandardOutput => _process.StandardOutput.BaseStream;
        public Stream? StandardError => _process.StandardError.BaseStream;
        public Sdk.IMxcStreamCloser? StandardOutputCloser => new LocalReadCloser(_process.StandardOutput.BaseStream);
        public Sdk.IMxcStreamCloser? StandardErrorCloser => new LocalReadCloser(_process.StandardError.BaseStream);
        public IReadOnlyList<string> Warnings => [];
        public Sdk.ExecutionMetadata? OutputMetadata => null;
        public Sdk.WaitResult Wait() => throw new NotSupportedException();
        public async Task<Sdk.WaitResult> WaitAsync(CancellationToken cancellationToken = default)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new Sdk.WaitResult { ExitCode = _process.ExitCode };
        }
        public Task<(Sdk.WaitResult Result, byte[] Stdout, byte[] Stderr)> WaitForExitWithOutputAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool TryGetExitCode(out int exitCode)
        {
            exitCode = _process.HasExited ? _process.ExitCode : 0;
            return _process.HasExited;
        }
        public void Kill()
        {
            if (!_process.HasExited)
            { _process.Kill(); }
        }
        public void Dispose()
        {
            _process.Dispose();
            _retainedJob?.Dispose();
        }
    }

    private sealed class LocalReadCloser(Stream stream) : Sdk.IMxcStreamCloser
    {
        public void Close() => stream.Dispose();
        public void Dispose() { }
    }

    private sealed class QuietStdio : IMxcHostStdio
    {
        public bool IsTerminal => false;
        public Sdk.MxcPtySize TerminalSize => new(30, 90);
        public void PrepareTerminal() { }
        public Stream OpenOutput() => Stream.Null;
        public Stream OpenError() => Stream.Null;
        public Task CopyInputAsync(Stream destination, CancellationToken cancellationToken) => destination.DisposeAsync().AsTask();
        public Task WatchSizeAsync(Action<Sdk.MxcPtySize> resize, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class QuietConsole : IHostConsole
    {
        public bool IsInteractive => false;
        public void InitializeUtf8() { }
        public IDisposable Capture(Action<string> log) => new MemoryStream();
    }

    private const string TreeScript = """
        param([string]$Root, [string]$Role, [string]$EarlyExit)
        $ErrorActionPreference = 'Stop'
        Add-Type @'
        using System.Runtime.InteropServices;
        using Microsoft.Win32.SafeHandles;
        public static class FixtureProcessAccess {
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool SetKernelObjectSecurity(SafeProcessHandle process, uint info, byte[] descriptor);
        }
        '@
        function Grant-FixtureObservation([int]$ProcessId) {
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            try { $sid = $identity.User.Value } finally { $identity.Dispose() }
            $sddl = "D:P(A;;GA;;;$sid)(A;;GA;;;SY)(A;;0x00101000;;;$env:OPENCLAW_FIXTURE_OBSERVER_SID)"
            $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($sddl)
            $bytes = [byte[]]::new($descriptor.BinaryLength)
            $descriptor.GetBinaryForm($bytes, 0)
            $handle = [FixtureProcessAccess]::OpenProcess(0x00040000, $false, $ProcessId)
            try {
                if ($handle.IsInvalid -or -not [FixtureProcessAccess]::SetKernelObjectSecurity($handle, 4, $bytes)) {
                    throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
                }
            } finally { $handle.Dispose() }
        }
        $hostObservation = -not [string]::IsNullOrEmpty($env:OPENCLAW_FIXTURE_OBSERVER_SID)
        if ($hostObservation) { Grant-FixtureObservation $PID }
        function Wait-File([string]$Name) {
            $watch = [IO.FileSystemWatcher]::new($Root, $Name)
            $signal = [guid]::NewGuid().ToString('N')
            $null = Register-ObjectEvent -InputObject $watch -EventName Created -SourceIdentifier $signal
            $watch.EnableRaisingEvents = $true
            try {
                if (-not [IO.File]::Exists((Join-Path $Root $Name))) {
                    $change = Wait-Event -SourceIdentifier $signal -Timeout 60
                    if ($null -eq $change) { throw "Fixture signal timed out: $Name" }
                }
            } finally {
                Unregister-Event -SourceIdentifier $signal
                Remove-Event -SourceIdentifier $signal -ErrorAction SilentlyContinue
                $watch.Dispose()
            }
        }
        [IO.File]::WriteAllText((Join-Path $Root "$Role.pid"), [string]$PID)
        if ($Role -eq 'parent') {
            $self = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
            if ($hostObservation) { Grant-FixtureObservation $self.ParentProcessId }
            [IO.File]::WriteAllText((Join-Path $Root 'helper.pid'), [string]$self.ParentProcessId)
            $helper = Get-CimInstance Win32_Process -Filter "ProcessId=$($self.ParentProcessId)"
            $mode = [regex]::Match([string]$helper.CommandLine, '--request-in-job=[0-9a-fA-F]{32}(?=["\s]|$)')
            if ($mode.Success) {
                [IO.File]::WriteAllText((Join-Path $Root 'invocation-mode.txt'), $mode.Value)
                $coordinator = Get-CimInstance Win32_Process -Filter "ProcessId=$($helper.ParentProcessId)"
                $coordinatorMode = [regex]::Match([string]$coordinator.CommandLine,
                    '--coordinate-invocation=[0-9a-fA-F]{32}(?=["\s]|$)')
                if ($coordinatorMode.Success) {
                    if ($hostObservation) { Grant-FixtureObservation $coordinator.ProcessId }
                    [IO.File]::WriteAllText((Join-Path $Root 'coordinator-process.txt'), [string]$coordinator.ProcessId)
                }
            }
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            try { [IO.File]::WriteAllText((Join-Path $Root 'identity.txt'), $identity.User.Value) }
            finally { $identity.Dispose() }
            $environment = @{
                Profile = $env:USERPROFILE
                Temp = [IO.Path]::GetTempPath()
                ProcessSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
                HostObservationGranted = $hostObservation
            } | ConvertTo-Json -Compress
            [IO.File]::WriteAllText((Join-Path $Root 'agent-environment.json'), $environment)
        }
        if ($Role -eq 'descendant') {
            [IO.File]::WriteAllText((Join-Path $Root 'ready'), '')
            if ($env:OPENCLAW_FIXTURE_ADMISSION_PROBE -eq '1') {
                Wait-File 'attempt-spawn'
                $rejected = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\cmd.exe'))
                $rejected.UseShellExecute = $false
                foreach ($value in @('/d','/c','echo admitted > "%OPENCLAW_FIXTURE_ADMITTED_PATH%"')) {
                    $rejected.ArgumentList.Add($value)
                }
                $rejected.Environment['OPENCLAW_FIXTURE_ADMITTED_PATH'] = Join-Path $Root 'admitted-effect'
                try {
                    $attempt = [Diagnostics.Process]::Start($rejected)
                    try { $attempt.WaitForExit(); $outcome = "STARTED exit=$($attempt.ExitCode)" }
                    finally { $attempt.Dispose() }
                } catch [ComponentModel.Win32Exception] {
                    $outcome = "REFUSED error=$($_.Exception.NativeErrorCode)"
                }
                [IO.File]::WriteAllText((Join-Path $Root 'admission-result.txt'), $outcome)
                [IO.File]::WriteAllText((Join-Path $Root 'admission-ready'), '')
            }
            Wait-File 'release'
            [IO.File]::WriteAllText((Join-Path $Root 'late-effect'), 'descendant ran')
            exit 0
        }
        $next = if ($Role -eq 'parent') { 'child' } else { 'descendant' }
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
        $start.UseShellExecute = $false
        foreach ($value in @('-NoLogo','-NoProfile','-NonInteractive','-File',$PSCommandPath,
            '-Root',$Root,'-Role',$next,'-EarlyExit','no')) { $start.ArgumentList.Add($value) }
        $child = [Diagnostics.Process]::Start($start)
        try {
            if ($Role -eq 'parent' -and $EarlyExit -eq 'yes') { Wait-File 'exit-parent'; exit 0 }
            $child.WaitForExit()
            exit $child.ExitCode
        } finally { $child.Dispose() }
        """;
}
