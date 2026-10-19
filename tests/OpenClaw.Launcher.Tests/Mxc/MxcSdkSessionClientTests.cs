using System.Text;
using OpenClaw.Launcher.Mxc;
using Sdk = Microsoft.Mxc.Sdk.V1;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcSdkSessionClientTests
{
    private static readonly MxcRuntimeLocation Runtime = new(
        @"C:\Package", @"C:\Package\mxc_ffi.dll", @"C:\Package\plm.exe", null);
    private static readonly MxcSandboxId Id = MxcSandboxId.Parse("iso:recorded-value");

    [Fact]
    public async Task ProvisionUsesThePackageIdentityAndRequiredNetworkPolicy()
    {
        var sdk = new FakeLifecycle();
        MxcProvisionResult result = await Create(sdk).ProvisionAsync(
            new MxcProvisionRequest("PFN:OpenClaw_fixture"), CancellationToken.None);

        Assert.Equal("PFN:OpenClaw_fixture", sdk.ProvisionRequest!.AppId);
        Assert.Equal(Sdk.NetworkAction.Allow, sdk.ProvisionRequest.Network.Egress?.Default);
        Assert.Null(sdk.ProvisionRequest.Network.Egress?.Allow);
        Assert.Null(sdk.ProvisionRequest.Network.Egress?.Deny);
        Assert.Equal(Sdk.NetworkAction.Allow, sdk.ProvisionRequest.Network.Ingress?.Default);
        Assert.Equal(Sdk.NetworkAction.Allow, sdk.ProvisionRequest.Network.Ingress?.HostLoopback);
        Assert.Equal("iso:minted", result.SandboxId.Value);
        Assert.Equal(new MxcProvisionMetadata("agent", "S-1-5-21-fixture", @"C:\Shared"), result.Metadata);
    }

    [Fact]
    public async Task CancellationCannotAbandonADispatchedProvision()
    {
        var entered = Signal();
        var release = Signal();
        var sdk = new FakeLifecycle
        {
            BeforeProvision = () =>
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        using var cancellation = new CancellationTokenSource();
        Task<MxcProvisionResult> provision = Create(sdk).ProvisionAsync(
            new MxcProvisionRequest("PFN:OpenClaw_fixture"), cancellation.Token);
        await entered.Task.ConfigureAwait(true);
        await cancellation.CancelAsync();
        Assert.False(provision.IsCompleted);
        release.SetResult();

        Assert.Equal("iso:minted", (await provision.ConfigureAwait(true)).SandboxId.Value);
    }

    [Fact]
    public async Task LifecycleReplaysTheFullRecordedId()
    {
        var sdk = new FakeLifecycle();
        MxcSdkSessionClient client = Create(sdk);
        await client.StartAsync(Id, CancellationToken.None);
        await client.StopAsync(Id, CancellationToken.None);
        await client.DeprovisionAsync(Id, CancellationToken.None);
        Assert.Equal(
            ["start:iso:recorded-value", "stop:iso:recorded-value", "deprovision:iso:recorded-value"],
            sdk.Calls);
    }

    [Theory]
    [InlineData(Sdk.ErrorCode.StaleId, nameof(MxcErrorCode.StaleId))]
    [InlineData(Sdk.ErrorCode.BackendUnavailable, nameof(MxcErrorCode.RuntimeUnavailable))]
    [InlineData(Sdk.ErrorCode.NotStarted, nameof(MxcErrorCode.BackendError))]
    [InlineData(Sdk.ErrorCode.MalformedRequest, nameof(MxcErrorCode.MalformedRequest))]
    [InlineData((Sdk.ErrorCode)9876, nameof(MxcErrorCode.Unknown))]
    public async Task TypedFailuresPreserveRecoveryAndOriginalEvidence(
        Sdk.ErrorCode sdkCode, string expected)
    {
        var failure = new Sdk.MxcException(sdkCode, "native operation failed");
        var sdk = new FakeLifecycle { StartFailure = failure };

        MxcException error = await Assert.ThrowsAsync<MxcException>(
            () => Create(sdk).StartAsync(Id, CancellationToken.None));

        Assert.Equal(Enum.Parse<MxcErrorCode>(expected), error.Code);
        Assert.Equal(sdkCode.ToString(), error.BackendCode);
        Assert.Same(failure, error.InnerException);
    }

    [Fact]
    public async Task NativeLoadFailureNamesTheExactRuntimeWithoutDispatch()
    {
        var sdk = new FakeLifecycle();
        var client = new MxcSdkSessionClient(
            Runtime, sdk, new FakeConsole([]), new FakeStdio(),
            prepareRuntime: _ => throw new BadImageFormatException("wrong native architecture"));

        MxcException error = await Assert.ThrowsAsync<MxcException>(
            () => client.StartAsync(Id, CancellationToken.None));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, error.Code);
        Assert.Contains(Runtime.NativeLibraryPath, error.Message, StringComparison.Ordinal);
        Assert.Empty(sdk.Calls);
    }

    [Fact]
    public async Task ForeignBackendIdsAreNotDispatched()
    {
        var sdk = new FakeLifecycle();
        await Assert.ThrowsAsync<MxcException>(() => Create(sdk).StartAsync(
            MxcSandboxId.Parse("wslc:foreign"), CancellationToken.None));
        Assert.Empty(sdk.Calls);
    }

    [Fact]
    public async Task CapturedExecutionClosesInputAndReturnsOutputWithoutParsingIt()
    {
        var process = new FakeProcess(
            """{"error":{"code":"stale_id","message":"workload data"}}""", "warning", 7);
        var sdk = new FakeLifecycle { Process = process };

        MxcExecutionResult result = await Create(sdk).ExecuteAsync(
            Id, new MxcExecutionRequest("helper.exe --request fixture.json"), CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("workload data", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("warning", result.StandardError);
        Assert.True(process.Input.Closed);
        Assert.True(process.Disposed);
        Assert.Equal("helper.exe --request fixture.json", sdk.Execution!.Command);
        Assert.Null(sdk.Execution.Environment);
    }

    [Fact]
    public async Task RedirectedExecutionKeepsStreamsSeparateAndDeliversInput()
    {
        var process = new FakeProcess("prompt \u00e9", "stderr \u4e2d", 4);
        var sdk = new FakeLifecycle { Process = process };
        var stdio = new FakeStdio { InputText = "typed \u00e9\n" };

        int result = await Create(sdk, stdio).ExecuteAttachedAsync(
            Id, new MxcExecutionRequest("helper.exe"), CancellationToken.None);

        Assert.Equal(4, result);
        Assert.Equal("prompt \u00e9", stdio.OutputText);
        Assert.Equal("stderr \u4e2d", stdio.ErrorText);
        Assert.Equal("typed \u00e9\n", process.Input.Text);
        Assert.True(process.Input.Closed);
        Assert.Contains("spawn:iso:recorded-value", sdk.Calls);
        Assert.DoesNotContain(sdk.Calls, call => call.StartsWith("pty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TerminalExecutionForwardsSizeAndRestoresTheConsole()
    {
        var release = Signal();
        var process = new FakeProcess("terminal output", string.Empty, 3) { Release = release.Task };
        var sdk = new FakeLifecycle { Process = process };
        var stdio = new FakeStdio { IsTerminal = true, WaitForCancellation = true };
        var events = new List<string>();
        var client = new MxcSdkSessionClient(
            Runtime, sdk, new FakeConsole(events), stdio, prepareRuntime: _ => { });

        Task<int> execution = client.ExecuteAttachedAsync(
            Id, new MxcExecutionRequest("helper.exe"), CancellationToken.None);
        await stdio.Watching.Task.ConfigureAwait(true);
        stdio.Resize!(new Sdk.MxcPtySize(40, 120));
        release.SetResult();

        Assert.Equal(3, await execution.ConfigureAwait(true));
        Assert.Equal(new Sdk.MxcPtySize(30, 90), sdk.InitialSize);
        Assert.Equal([new Sdk.MxcPtySize(40, 120)], sdk.Resizes);
        Assert.True(stdio.Prepared);
        Assert.True(stdio.InputJoined);
        Assert.Equal(["capture", "utf8", "restore"], events);
        Assert.Contains("pty:iso:recorded-value", sdk.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreCancelledExecutionCreatesNoProcess(bool terminal)
    {
        var sdk = new FakeLifecycle();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(
            sdk, new FakeStdio { IsTerminal = terminal }).ExecuteAttachedAsync(
                Id, new MxcExecutionRequest("helper.exe"), cancellation.Token));
        Assert.Empty(sdk.Calls);
    }

    [Fact]
    public async Task CancellationDuringSpawnWaitsForAndReapsTheLateProcess()
    {
        var entered = Signal();
        var release = Signal();
        var process = new FakeProcess(string.Empty, string.Empty, 1) { WaitForKill = true };
        var sdk = new FakeLifecycle
        {
            Process = process,
            BeforeSpawn = () =>
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        using var cancellation = new CancellationTokenSource();
        Task<MxcExecutionResult> execution = Create(sdk).ExecuteAsync(
            Id, new MxcExecutionRequest("helper.exe"), cancellation.Token);
        await entered.Task.ConfigureAwait(true);
        await cancellation.CancelAsync();
        Assert.False(execution.IsCompleted);
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(process.Killed);
        Assert.True(process.WaitCompleted);
        Assert.True(process.Disposed);
        Assert.DoesNotContain(sdk.Calls, call => call.StartsWith("stop", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForegroundCancellationJoinsInputAndKillsOnlyItsProcess(bool terminal)
    {
        var process = new FakeProcess(string.Empty, string.Empty, 1) { WaitForKill = true };
        var sdk = new FakeLifecycle { Process = process };
        var stdio = new FakeStdio { IsTerminal = terminal, WaitForCancellation = true };
        using var cancellation = new CancellationTokenSource();
        Task<int> execution = Create(sdk, stdio).ExecuteAttachedAsync(
            Id, new MxcExecutionRequest("helper.exe"), cancellation.Token);
        await process.WaitStarted.Task.ConfigureAwait(true);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(stdio.InputJoined);
        Assert.True(process.Killed);
        Assert.True(process.WaitCompleted);
        Assert.True(process.Disposed);
        Assert.DoesNotContain(sdk.Calls, call => call.StartsWith("stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClosedDownstreamPipeDoesNotBlockTheWorkload()
    {
        var process = new FakeProcess(new string('x', 200_000), string.Empty, 0);
        var sdk = new FakeLifecycle { Process = process };
        var stdio = new FakeStdio { Output = new FailingWriter(109) };
        var log = new List<string>();

        int result = await Create(sdk, stdio, log.Add).ExecuteAttachedAsync(
            Id, new MxcExecutionRequest("helper.exe"), CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(200_000, process.Output.BytesRead);
        Assert.Contains(log, entry => entry.Contains("downstream output pipe closed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnexpectedRelayFailureTerminatesAndReapsTheWorkload()
    {
        var process = new FakeProcess("output", string.Empty, 0) { WaitForKill = true };
        var sdk = new FakeLifecycle { Process = process };
        var stdio = new FakeStdio { Output = new FailingWriter(5), WaitForCancellation = true };

        await Assert.ThrowsAsync<IOException>(() => Create(sdk, stdio).ExecuteAttachedAsync(
            Id, new MxcExecutionRequest("helper.exe"), CancellationToken.None));

        Assert.True(process.Killed);
        Assert.True(process.WaitCompleted);
        Assert.True(stdio.InputJoined);
    }

    [Fact]
    public async Task DiscoveryUsesIsolationSessionNotAnotherAvailableBackend()
    {
        MxcBackendProbe probe = await MxcSdkSessionClient.ProbeBackendAsync(
            Runtime,
            () => [new Sdk.AvailableBackend { Backend = Sdk.ContainmentBackend.ProcessContainer }],
            () => "native-fixture", _ => { }, CancellationToken.None);
        Assert.False(probe.IsolationSessionAvailable);
        Assert.Equal("native-fixture", probe.NativeVersion);
    }

    private static MxcSdkSessionClient Create(
        FakeLifecycle sdk, FakeStdio? stdio = null, Action<string>? log = null) =>
        new(Runtime, sdk, new FakeConsole([]), stdio ?? new FakeStdio(),
            log, prepareRuntime: _ => { });

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeLifecycle : IMxcSdkLifecycle
    {
        public List<string> Calls { get; } = [];
        public Sdk.IsolationSessionProvisionRequest? ProvisionRequest { get; private set; }
        public Sdk.ExecutionRequest? Execution { get; private set; }
        public Action? BeforeProvision { get; init; }
        public Action? BeforeSpawn { get; init; }
        public Exception? StartFailure { get; init; }
        public FakeProcess Process { get; init; } = new(string.Empty, string.Empty, 0);
        public Sdk.MxcPtySize InitialSize { get; private set; }
        public List<Sdk.MxcPtySize> Resizes { get; } = [];

        public Sdk.ProvisionResult Provision(Sdk.IsolationSessionProvisionRequest request)
        {
            BeforeProvision?.Invoke();
            Calls.Add("provision");
            ProvisionRequest = request;
            return new Sdk.ProvisionResult
            {
                ContainerId = new Sdk.ContainerId("iso:minted"),
                Metadata = new Sdk.IsolationSessionProvisionMetadata
                {
                    AgentUserName = "agent",
                    AgentUserSid = "S-1-5-21-fixture",
                    EphemeralWorkspacePath = @"C:\Shared"
                }
            };
        }

        public Sdk.LifecycleResult Start(Sdk.ContainerId id)
        {
            Calls.Add($"start:{id.Value}");
            if (StartFailure is not null)
            { throw StartFailure; }
            return new Sdk.LifecycleResult();
        }

        public Sdk.IMxcProcess Spawn(Sdk.ContainerId id, Sdk.ExecutionRequest request)
        {
            BeforeSpawn?.Invoke();
            Calls.Add($"spawn:{id.Value}");
            Execution = request;
            return Process;
        }

        public MxcTerminalProcess SpawnTerminal(
            Sdk.ContainerId id, Sdk.ExecutionRequest request, Sdk.MxcPtySize size)
        {
            Calls.Add($"pty:{id.Value}");
            Execution = request;
            InitialSize = size;
            return new MxcTerminalProcess(Process, Resizes.Add);
        }

        public Sdk.LifecycleResult Stop(Sdk.ContainerId id)
        {
            Calls.Add($"stop:{id.Value}");
            return new Sdk.LifecycleResult();
        }

        public Sdk.LifecycleResult Deprovision(Sdk.ContainerId id)
        {
            Calls.Add($"deprovision:{id.Value}");
            return new Sdk.LifecycleResult();
        }
    }

    private sealed class FakeProcess(string stdout, string stderr, int exitCode) : Sdk.IMxcProcess
    {
        private readonly TaskCompletionSource _killed = Signal();
        public RecordingInput Input { get; } = new();
        public RecordingOutput Output { get; } = new(Encoding.UTF8.GetBytes(stdout));
        public Stream? StandardError { get; } = new MemoryStream(Encoding.UTF8.GetBytes(stderr));
        public Stream? StandardInput => Input;
        public Stream? StandardOutput => Output;
        public Sdk.IMxcStreamCloser? StandardOutputCloser => null;
        public Sdk.IMxcStreamCloser? StandardErrorCloser => null;
        public IReadOnlyList<string> Warnings => [];
        public Sdk.ExecutionMetadata? OutputMetadata => null;
        public uint Id => 0;
        public bool WaitForKill { get; init; }
        public Task? Release { get; init; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public bool WaitCompleted { get; private set; }
        public TaskCompletionSource WaitStarted { get; } = Signal();
        public Sdk.WaitResult Wait() => throw new NotSupportedException();

        public async Task<Sdk.WaitResult> WaitAsync(CancellationToken cancellationToken = default)
        {
            WaitStarted.TrySetResult();
            if (WaitForKill)
            { await _killed.Task.ConfigureAwait(false); }
            if (Release is not null)
            { await Release.ConfigureAwait(false); }
            WaitCompleted = true;
            return new Sdk.WaitResult { ExitCode = exitCode };
        }

        public async Task<(Sdk.WaitResult Result, byte[] Stdout, byte[] Stderr)> WaitForExitWithOutputAsync(
            CancellationToken cancellationToken = default)
        {
            Sdk.WaitResult result = await WaitAsync(cancellationToken).ConfigureAwait(false);
            return (result, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr));
        }

        public bool TryGetExitCode(out int code)
        {
            code = exitCode;
            return WaitCompleted;
        }

        public void Kill()
        {
            Killed = true;
            _killed.TrySetResult();
        }

        public void Dispose()
        {
            Disposed = true;
            Input.Dispose();
            Output.Dispose();
            StandardError?.Dispose();
        }
    }

    private sealed class RecordingInput : MemoryStream
    {
        public bool Closed { get; private set; }
        public string Text => Encoding.UTF8.GetString(ToArray());
        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingOutput(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }

    private sealed class FailingWriter(int error) : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Fixture output failed.", unchecked((int)0x80070000) | error));
    }

    private sealed class FakeStdio : IMxcHostStdio
    {
        public bool IsTerminal { get; init; }
        public Sdk.MxcPtySize TerminalSize => new(30, 90);
        public bool Prepared { get; private set; }
        public bool WaitForCancellation { get; init; }
        public bool InputJoined { get; private set; }
        public string InputText { get; init; } = string.Empty;
        public MemoryStream Output { get; init; } = new();
        public MemoryStream Error { get; } = new();
        public string OutputText => Encoding.UTF8.GetString(Output.ToArray());
        public string ErrorText => Encoding.UTF8.GetString(Error.ToArray());
        public TaskCompletionSource Watching { get; } = Signal();
        public Action<Sdk.MxcPtySize>? Resize { get; private set; }
        public void PrepareTerminal() => Prepared = true;
        public Stream OpenOutput() => Output;
        public Stream OpenError() => Error;

        public async Task CopyInputAsync(Stream destination, CancellationToken cancellationToken)
        {
            try
            {
                await destination.WriteAsync(Encoding.UTF8.GetBytes(InputText), cancellationToken).ConfigureAwait(false);
                if (WaitForCancellation)
                {
                    var stop = Signal();
                    using CancellationTokenRegistration registration = cancellationToken.Register(() => stop.TrySetResult());
                    await stop.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                await destination.DisposeAsync().ConfigureAwait(false);
                InputJoined = true;
            }
        }

        public async Task WatchSizeAsync(Action<Sdk.MxcPtySize> resize, CancellationToken cancellationToken)
        {
            Resize = resize;
            Watching.TrySetResult();
            var stop = Signal();
            using CancellationTokenRegistration registration = cancellationToken.Register(() => stop.TrySetResult());
            await stop.Task.ConfigureAwait(false);
        }
    }

    private sealed class FakeConsole(List<string> events) : IHostConsole
    {
        public bool IsInteractive => true;
        public void InitializeUtf8() => events.Add("utf8");
        public IDisposable Capture(Action<string> log)
        {
            events.Add("capture");
            return new Restore(events);
        }

        private sealed class Restore(List<string> events) : IDisposable
        {
            public void Dispose() => events.Add("restore");
        }
    }
}
