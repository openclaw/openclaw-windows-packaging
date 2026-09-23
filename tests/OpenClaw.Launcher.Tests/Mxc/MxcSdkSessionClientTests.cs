using System.Text;
using OpenClaw.Launcher.Mxc;
using Sdk = Microsoft.Mxc.Sdk;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcSdkSessionClientTests
{
    private static readonly MxcRuntimeLocation Runtime = new(
        @"C:\Package",
        @"C:\Package\mxc_ffi.dll",
        @"C:\Package\plm.exe");

    private static readonly MxcSandboxId SandboxId = MxcSandboxId.Parse("iso:agent");

    [Fact]
    public async Task ProvisionRequestsAnIsolationSessionForThePackageIdentity()
    {
        var lifecycle = new FakeLifecycle
        {
            ProvisionResult = new Sdk.ProvisionResult
            {
                SandboxId = new Sdk.SandboxId("iso:minted"),
                IsolationSessionMetadata = new Sdk.IsolationSessionProvisionMetadata
                {
                    AgentUserName = "agent_1",
                    AgentUserSid = "S-1-5-21-1",
                    EphemeralWorkspacePath = @"C:\Shared"
                }
            }
        };
        var client = CreateClient(lifecycle);

        MxcProvisionResult result = await client.ProvisionAsync(
            new MxcProvisionRequest("PFN:OpenClaw_abc"),
            CancellationToken.None);

        Assert.Equal(Sdk.StateAwareContainment.IsolationSession, lifecycle.ProvisionedContainment);
        var options = Assert.IsType<Sdk.IsolationSessionProvisionOptions>(lifecycle.ProvisionOptions);
        Assert.Equal("PFN:OpenClaw_abc", options.AppId);

        // IsolationSession accepts only the explicit all-allow posture; any
        // other network policy is rejected before a session is created.
        Assert.Equal(Sdk.NetworkAction.Allow, options.Network.Egress?.Default);
        Assert.Null(options.Network.Egress?.Allow);
        Assert.Null(options.Network.Egress?.Deny);
        Assert.Equal(Sdk.NetworkAction.Allow, options.Network.Ingress?.Default);
        Assert.Equal(Sdk.NetworkAction.Allow, options.Network.Ingress?.HostLoopback);

        Assert.Equal("iso:minted", result.SandboxId.Value);
        Assert.Equal(
            new MxcProvisionMetadata("agent_1", "S-1-5-21-1", @"C:\Shared"),
            result.Metadata);
    }

    [Fact]
    public async Task PartialProvisionMetadataIsNotReported()
    {
        var lifecycle = new FakeLifecycle
        {
            ProvisionResult = new Sdk.ProvisionResult
            {
                SandboxId = new Sdk.SandboxId("iso:minted"),
                IsolationSessionMetadata = new Sdk.IsolationSessionProvisionMetadata
                {
                    AgentUserName = "agent_1"
                }
            }
        };

        MxcProvisionResult result = await CreateClient(lifecycle).ProvisionAsync(
            new MxcProvisionRequest("PFN:OpenClaw_abc"),
            CancellationToken.None);

        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task ProvisionWithoutAnApplicationIdIsRejectedBeforeDispatch()
    {
        var lifecycle = new FakeLifecycle();

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => CreateClient(lifecycle).ProvisionAsync(
                new MxcProvisionRequest(string.Empty),
                CancellationToken.None));

        Assert.Equal(MxcErrorCode.PolicyValidation, exception.Code);
        Assert.Empty(lifecycle.Calls);
    }

    [Fact]
    public async Task ADispatchedProvisionIsNotAbandonedByCancellation()
    {
        // Abandoning the call would drop the id of a sandbox the backend had
        // already created, leaving an agent account nothing can deprovision.
        using var release = new ManualResetEventSlim();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new FakeLifecycle
        {
            ProvisionResult = new Sdk.ProvisionResult { SandboxId = new Sdk.SandboxId("iso:late") },
            BeforeProvision = () =>
            {
                dispatched.SetResult();
                release.Wait();
            }
        };
        using var cancellation = new CancellationTokenSource();

        Task<MxcProvisionResult> provision = CreateClient(lifecycle).ProvisionAsync(
            new MxcProvisionRequest("PFN:OpenClaw_abc"),
            cancellation.Token);
        await dispatched.Task.ConfigureAwait(true);
        await cancellation.CancelAsync();
        release.Set();

        MxcProvisionResult result = await provision.ConfigureAwait(true);
        Assert.Equal("iso:late", result.SandboxId.Value);
    }

    [Theory]
    [InlineData(Sdk.ErrorCode.StaleId, nameof(MxcErrorCode.StaleId))]
    [InlineData(Sdk.ErrorCode.MalformedId, nameof(MxcErrorCode.MalformedId))]
    [InlineData(Sdk.ErrorCode.PolicyValidation, nameof(MxcErrorCode.PolicyValidation))]
    [InlineData(Sdk.ErrorCode.MalformedRequest, nameof(MxcErrorCode.MalformedRequest))]
    [InlineData(Sdk.ErrorCode.UnsupportedContainment, nameof(MxcErrorCode.UnsupportedContainment))]
    [InlineData(Sdk.ErrorCode.BackendError, nameof(MxcErrorCode.BackendError))]
    [InlineData(Sdk.ErrorCode.BackendUnavailable, nameof(MxcErrorCode.RuntimeUnavailable))]
    [InlineData(Sdk.ErrorCode.AlreadyStarted, nameof(MxcErrorCode.Unknown))]
    public async Task SdkFailuresKeepTheirRecoveryClassification(
        Sdk.ErrorCode sdkCode,
        string expectedName)
    {
        var lifecycle = new FakeLifecycle
        {
            StartFailure = new Sdk.MxcException(sdkCode, "backend said no")
        };

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => CreateClient(lifecycle).StartAsync(SandboxId, CancellationToken.None));

        Assert.Equal(Enum.Parse<MxcErrorCode>(expectedName), exception.Code);
        Assert.Equal(sdkCode.ToString(), exception.BackendCode);
        Assert.Contains("backend said no", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANativeUnitThatCannotLoadIsReportedAsAnUnavailableRuntime()
    {
        var lifecycle = new FakeLifecycle
        {
            StartFailure = new DllNotFoundException("mxc_ffi could not be found")
        };

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => CreateClient(lifecycle).StartAsync(SandboxId, CancellationToken.None));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, exception.Code);
        Assert.Contains(Runtime.NativeLibraryPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecyclePhasesAddressTheRecordedSandbox()
    {
        var lifecycle = new FakeLifecycle();
        var client = CreateClient(lifecycle);

        await client.StartAsync(SandboxId, CancellationToken.None);
        await client.StopAsync(SandboxId, CancellationToken.None);
        await client.DeprovisionAsync(SandboxId, CancellationToken.None);

        Assert.Equal(["start:iso:agent", "stop:iso:agent", "deprovision:iso:agent"], lifecycle.Calls);
    }

    [Fact]
    public async Task ANonIsolationSessionIdIsRejectedBeforeDispatch()
    {
        var lifecycle = new FakeLifecycle();

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => CreateClient(lifecycle).StartAsync(
                MxcSandboxId.Parse("wsb:other"),
                CancellationToken.None));

        Assert.Equal(MxcErrorCode.MalformedId, exception.Code);
        Assert.Empty(lifecycle.Calls);
    }

    [Fact]
    public async Task BufferedExecutionReturnsTheWorkloadsOutputAndExitCode()
    {
        var lifecycle = new FakeLifecycle
        {
            RunResult = new Sdk.RunResult { ExitCode = 7, Stdout = "out", Stderr = "err" }
        };

        MxcExecutionResult result = await CreateClient(lifecycle).ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe request.json"),
            CancellationToken.None);

        Assert.Equal(new MxcExecutionResult(7, "out", "err"), result);
        Assert.Equal(["exec-buffered:iso:agent:helper.exe request.json"], lifecycle.Calls);
    }

    [Fact]
    public async Task AnEmptyCommandLineIsRejectedBeforeDispatch()
    {
        var lifecycle = new FakeLifecycle();

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => CreateClient(lifecycle).ExecuteAsync(
                SandboxId,
                new MxcExecutionRequest(" "),
                CancellationToken.None));

        Assert.Equal(MxcErrorCode.PolicyValidation, exception.Code);
        Assert.Empty(lifecycle.Calls);
    }

    [Fact]
    public async Task AConsoleInvocationAttachesTheWorkloadInsideConsoleCapture()
    {
        var events = new List<string>();
        var lifecycle = new FakeLifecycle
        {
            AttachedResult = new Sdk.SandboxWaitResult { ExitCode = 3 },
            BeforeAttached = () => events.Add("attached")
        };
        var console = new RecordingHostConsole(events);
        var client = new MxcSdkSessionClient(
            Runtime,
            lifecycle,
            console,
            new FakeStdio { InputAndOutputAreConsoles = true });

        int exitCode = await client.ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe request.json"),
            CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Equal(["capture", "utf8", "attached", "restore"], events);
        Assert.Equal(["exec-attached:iso:agent:helper.exe request.json"], lifecycle.Calls);
    }

    [Fact]
    public async Task ARedirectedInvocationRelaysEveryStreamAndTheExitCode()
    {
        // The SDK refuses an attached execution unless both stdin and stdout
        // are terminals, so redirected and piped invocations must stream.
        var process = new FakeProcess(
            standardOutput: "workload output",
            standardError: "workload error",
            exitCode: 4);
        var lifecycle = new FakeLifecycle { Process = process };
        var stdio = new FakeStdio { Input = "typed input" };

        int exitCode = await CreateClient(lifecycle, stdio).ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe request.json"),
            CancellationToken.None);
        await process.Input.Closed.ConfigureAwait(true);

        Assert.Equal(4, exitCode);
        Assert.Equal(["exec-streaming:iso:agent:helper.exe request.json"], lifecycle.Calls);
        Assert.Equal("workload output", stdio.OutputText);
        Assert.Equal("workload error", stdio.ErrorText);
        Assert.Equal("typed input", process.Input.Text);
    }

    [Fact]
    public async Task ARelayKeepsDrainingAfterTheHostReaderGoesAway()
    {
        // `openclaw ... | Select-Object -First 1` closes the pipe early. The
        // workload must still run to completion rather than block or fault.
        var process = new FakeProcess(
            standardOutput: new string('x', 200_000),
            standardError: string.Empty,
            exitCode: 0);
        var lifecycle = new FakeLifecycle { Process = process };
        var stdio = new FakeStdio { Output = new ClosedPipeStream() };

        int exitCode = await CreateClient(lifecycle, stdio).ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe request.json"),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, process.StandardOutputRemaining);
    }

    [Fact]
    public async Task CancellingARelayedInvocationKillsOnlyItsWorkload()
    {
        var process = new FakeProcess(string.Empty, string.Empty, exitCode: 1)
        {
            WaitsForKill = true
        };
        var lifecycle = new FakeLifecycle { Process = process };
        using var cancellation = new CancellationTokenSource();

        Task<int> invocation = CreateClient(lifecycle).ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe request.json"),
            cancellation.Token);
        await process.WaitStarted.ConfigureAwait(true);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.True(process.Killed);
        Assert.DoesNotContain(lifecycle.Calls, call => call.StartsWith("stop", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APreCancelledInvocationDispatchesNothing(bool consoles)
    {
        var lifecycle = new FakeLifecycle();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient(lifecycle, new FakeStdio { InputAndOutputAreConsoles = consoles })
                .ExecuteAttachedAsync(
                    SandboxId,
                    new MxcExecutionRequest("helper.exe request.json"),
                    cancellation.Token));

        Assert.Empty(lifecycle.Calls);
    }

    [Fact]
    public async Task BackendProbeReportsTheIsolationSessionBackend()
    {
        MxcBackendProbe probe = await MxcSdkSessionClient.ProbeBackendAsync(
            Runtime,
            () =>
            [
                new Sdk.AvailableBackend { Backend = Sdk.ContainmentBackend.ProcessContainer },
                new Sdk.AvailableBackend
                {
                    Backend = Sdk.ContainmentBackend.IsolationSession,
                    Warnings = ["preview"]
                }
            ],
            CancellationToken.None);

        Assert.True(probe.IsolationSessionAvailable);
        Assert.Equal(["preview"], probe.Warnings);
    }

    [Fact]
    public async Task BackendProbeReportsAHostWithoutIsolationSession()
    {
        MxcBackendProbe probe = await MxcSdkSessionClient.ProbeBackendAsync(
            Runtime,
            () => [new Sdk.AvailableBackend { Backend = Sdk.ContainmentBackend.ProcessContainer }],
            CancellationToken.None);

        Assert.False(probe.IsolationSessionAvailable);
    }

    [Fact]
    public async Task BackendProbeReportsANativeUnitThatCannotLoad()
    {
        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => MxcSdkSessionClient.ProbeBackendAsync(
                Runtime,
                () => throw new BadImageFormatException("wrong architecture"),
                CancellationToken.None));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, exception.Code);
    }

    private static MxcSdkSessionClient CreateClient(
        FakeLifecycle lifecycle,
        FakeStdio? stdio = null) =>
        new(
            Runtime,
            lifecycle,
            new RecordingHostConsole([]),
            stdio ?? new FakeStdio());

    private sealed class FakeLifecycle : Sdk.ISandboxLifecycle
    {
        public List<string> Calls { get; } = [];

        public Sdk.StateAwareContainment? ProvisionedContainment { get; private set; }

        public Sdk.StateAwareProvisionOptions? ProvisionOptions { get; private set; }

        public Sdk.ProvisionResult ProvisionResult { get; init; } =
            new() { SandboxId = new Sdk.SandboxId("iso:fixture") };

        public Action? BeforeProvision { get; init; }

        public Exception? StartFailure { get; init; }

        public Sdk.RunResult RunResult { get; init; } = new();

        public Sdk.SandboxWaitResult AttachedResult { get; init; }

        public Action? BeforeAttached { get; init; }

        public FakeProcess? Process { get; init; }

        public Sdk.ProvisionResult ProvisionSandbox(
            Sdk.StateAwareContainment containment,
            Sdk.StateAwareProvisionOptions? options = null)
        {
            BeforeProvision?.Invoke();
            Calls.Add("provision");
            ProvisionedContainment = containment;
            ProvisionOptions = options;
            return ProvisionResult;
        }

        public void StartSandbox(Sdk.SandboxId id, Sdk.StateAwarePhaseOptions? options = null)
        {
            Calls.Add($"start:{id.Value}");
            if (StartFailure is not null)
            {
                throw StartFailure;
            }
        }

        public Sdk.ISandboxProcess ExecInSandbox(
            Sdk.SandboxId id,
            string command,
            Sdk.StateAwareExecOptions? options = null)
        {
            Calls.Add($"exec-streaming:{id.Value}:{command}");
            return Process ?? throw new InvalidOperationException("No fixture process.");
        }

        public Sdk.SandboxWaitResult ExecInSandboxAttached(
            Sdk.SandboxId id,
            string command,
            Sdk.StateAwareExecOptions? options = null)
        {
            BeforeAttached?.Invoke();
            Calls.Add($"exec-attached:{id.Value}:{command}");
            return AttachedResult;
        }

        public Task<Sdk.RunResult> ExecInSandboxAsync(
            Sdk.SandboxId id,
            string command,
            CancellationToken cancellationToken = default) =>
            ExecInSandboxAsync(id, command, null, cancellationToken);

        public Task<Sdk.RunResult> ExecInSandboxAsync(
            Sdk.SandboxId id,
            string command,
            Sdk.StateAwareExecOptions? options,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"exec-buffered:{id.Value}:{command}");
            return Task.FromResult(RunResult);
        }

        public void StopSandbox(Sdk.SandboxId id, Sdk.StateAwarePhaseOptions? options = null) =>
            Calls.Add($"stop:{id.Value}");

        public void DeprovisionSandbox(Sdk.SandboxId id, Sdk.StateAwarePhaseOptions? options = null) =>
            Calls.Add($"deprovision:{id.Value}");

        public void DryRunProvisionSandbox(
            Sdk.StateAwareContainment containment,
            Sdk.StateAwareProvisionOptions? options = null) =>
            throw new NotSupportedException();

        public void DryRunStartSandbox(Sdk.SandboxId id, Sdk.StateAwarePhaseOptions? options = null) =>
            throw new NotSupportedException();

        public void DryRunExecInSandbox(
            Sdk.SandboxId id,
            string command,
            Sdk.StateAwareExecOptions? options = null) =>
            throw new NotSupportedException();

        public void DryRunStopSandbox(Sdk.SandboxId id, Sdk.StateAwarePhaseOptions? options = null) =>
            throw new NotSupportedException();

        public void DryRunDeprovisionSandbox(
            Sdk.SandboxId id,
            Sdk.StateAwarePhaseOptions? options = null) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProcess(string standardOutput, string standardError, int exitCode)
        : Sdk.ISandboxProcess
    {
        private readonly MemoryStream _standardOutput = new(Encoding.UTF8.GetBytes(standardOutput));
        private readonly TaskCompletionSource _waitStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _killed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WaitsForKill { get; init; }

        public bool Killed { get; private set; }

        public Task WaitStarted => _waitStarted.Task;

        public RecordingInputStream Input { get; } = new();

        public long StandardOutputRemaining => _standardOutput.Length - _standardOutput.Position;

        public uint Id => 42;

        public Stream? StandardInput => Input;

        public Stream? StandardOutput => _standardOutput;

        public Stream? StandardError { get; } = new MemoryStream(Encoding.UTF8.GetBytes(standardError));

        public Sdk.ISandboxStreamCloser? StandardOutputCloser => null;

        public Sdk.ISandboxStreamCloser? StandardErrorCloser => null;

        public IReadOnlyList<string> Warnings => [];

        public Sdk.SandboxOutputMetadata? OutputMetadata => null;

        public Sdk.SandboxWaitResult Wait() => throw new NotSupportedException();

        public async Task<Sdk.SandboxWaitResult> WaitAsync(CancellationToken cancellationToken = default)
        {
            _waitStarted.TrySetResult();
            if (WaitsForKill)
            {
                await _killed.Task.ConfigureAwait(false);
            }

            return new Sdk.SandboxWaitResult { ExitCode = exitCode };
        }

        public bool TryGetExitCode(out int code)
        {
            code = exitCode;
            return true;
        }

        public Task<(Sdk.SandboxWaitResult Result, byte[] Stdout, byte[] Stderr)> WaitForExitWithOutputAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Kill()
        {
            Killed = true;
            _killed.TrySetResult();
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingInputStream : MemoryStream
    {
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Closed => _closed.Task;

        public string Text => Encoding.UTF8.GetString(ToArray());

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class ClosedPipeStream : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new IOException("The pipe has been ended.");

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("The pipe has been ended.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The pipe has been ended."));
    }

    private sealed class FakeStdio : IMxcHostStdio
    {
        public bool InputAndOutputAreConsoles { get; init; }

        public string Input { get; init; } = string.Empty;

        public Stream Output { get; init; } = new MemoryStream();

        public string OutputText => Encoding.UTF8.GetString(((MemoryStream)Output).ToArray());

        public MemoryStream Error { get; } = new();

        public string ErrorText => Encoding.UTF8.GetString(Error.ToArray());

        public Stream OpenInput() => new MemoryStream(Encoding.UTF8.GetBytes(Input));

        public Stream OpenOutput() => Output;

        public Stream OpenError() => Error;
    }

    private sealed class RecordingHostConsole(List<string> events) : IHostConsole
    {
        public bool IsInteractive => true;

        public IDisposable Capture(Action<string> log)
        {
            events.Add("capture");
            return new Restore(events);
        }

        public void InitializeUtf8() => events.Add("utf8");

        private sealed class Restore(List<string> events) : IDisposable
        {
            public void Dispose() => events.Add("restore");
        }
    }
}
