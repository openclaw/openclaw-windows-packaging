using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Launcher.Mxc;
using SdkMxcException = Microsoft.Mxc.Sdk.V1.MxcException;
using LauncherMxcException = OpenClaw.Launcher.Mxc.MxcException;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcSdkSessionClientTests
{
    private static readonly MxcRuntimeLocation Runtime = new(
        @"C:\package\mxc\x64",
        @"C:\package\mxc\x64\mxc_ffi.dll",
        new MxcRuntimeProvenance("Microsoft.Mxc.Sdk", "1.0.0", "x64"));

    private static readonly MxcSandboxId SandboxId = MxcSandboxId.Parse("iso:opaque:value");

    [Fact]
    public async Task ProvisionUsesStableSdkRequestAndPreservesReturnedIdentityAndMetadata()
    {
        var sdk = new FakeMxcSdkApi();
        var client = CreateClient(sdk);

        MxcProvisionResult result = await client.ProvisionAsync(
            new MxcProvisionRequest("PFN:Contoso.App_8wekyb3d8bbwe"),
            CancellationToken.None);

        Assert.Equal("iso:minted", result.SandboxId.Value);
        Assert.Equal("agent_fixture", result.Metadata!.AgentUserName);
        Assert.Equal("S-1-5-21-0-0-0-1234", result.Metadata.AgentUserSid);
        Assert.Equal(@"C:\fixture\workspace", result.Metadata.EphemeralWorkspacePath);
        IsolationSessionProvisionRequest request = Assert.IsType<IsolationSessionProvisionRequest>(
            sdk.ProvisionRequest);
        Assert.Equal("PFN:Contoso.App_8wekyb3d8bbwe", request.AppId);
        Assert.Equal(NetworkAction.Allow, request.Network.Egress!.Default);
        Assert.Null(request.Network.Egress.Allow);
        Assert.Null(request.Network.Egress.Deny);
        Assert.Equal(NetworkAction.Allow, request.Network.Ingress!.Default);
        Assert.Equal(NetworkAction.Allow, request.Network.Ingress.HostLoopback);
    }

    [Fact]
    public async Task ProvisionWithoutAnApplicationIdentityIsRejectedBeforeSdkCall()
    {
        var sdk = new FakeMxcSdkApi();
        var client = CreateClient(sdk);

        LauncherMxcException exception = await Assert.ThrowsAsync<LauncherMxcException>(() =>
            client.ProvisionAsync(new MxcProvisionRequest(" "), CancellationToken.None));

        Assert.Equal(MxcErrorCode.PolicyValidation, exception.Code);
        Assert.Empty(sdk.Calls);
    }

    [Fact]
    public async Task LifecyclePhasesReplayTheOpaqueIdWithoutAddingProvisionOptions()
    {
        var sdk = new FakeMxcSdkApi();
        var client = CreateClient(sdk);

        await client.StartAsync(SandboxId, CancellationToken.None);
        await client.StopAsync(SandboxId, CancellationToken.None);
        await client.DeprovisionAsync(SandboxId, CancellationToken.None);

        Assert.Equal(["start", "stop", "deprovision"], sdk.Calls);
        Assert.Equal(SandboxId.Value, sdk.LastContainerId!.Value.Value);
    }

    [Fact]
    public async Task CapturedExecutionReturnsGuestOutputAndExitCodeUnchanged()
    {
        const string stdout = "{\"error\":\"guest output\"}";
        const string stderr = "{\"error\":\"guest diagnostic\"}";
        var sdk = new FakeMxcSdkApi
        {
            Process = new FakeMxcProcess(stdout, stderr, exitCode: 7)
        };
        var client = CreateClient(sdk);

        MxcExecutionResult result = await client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe --request request.json"),
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(stdout, result.StandardOutput);
        Assert.Equal(stderr, result.StandardError);
        Assert.Equal("helper.exe --request request.json", sdk.LastExecutionRequest!.Command);
        Assert.True(sdk.Process.Disposed);
    }

    [Fact]
    public async Task CapturedExecutionClosesUnusedStdinSoGuestCanExitAtEof()
    {
        var process = new FakeMxcProcess(
            standardOutput: "captured",
            completeImmediately: false,
            completeWhenStandardInputCloses: true);
        var sdk = new FakeMxcSdkApi { Process = process };
        var client = CreateClient(sdk);

        MxcExecutionResult result = await client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("guest reads stdin to EOF"),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("captured", result.StandardOutput);
        Assert.True(process.StandardInputStream.IsClosed);
        Assert.True(process.WaitObserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalAttachedExitCancelsInputAndResizeWithoutInterruptingOutput(bool interactive)
    {
        var output = new FakeMxcBlockingOutputStream("complete output tail");
        var process = new FakeMxcProcess(standardOutputStream: output);
        var pty = new FakeMxcPtySession(outputStream: output);
        process.OutputCloser.OnClose = output.InterruptRead;
        pty.OutputCloser.OnClose = output.InterruptRead;
        var sdk = new FakeMxcSdkApi { Process = process, PtyProcess = pty };
        var inputStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new FakeMxcHostTerminal(interactive)
        {
            InputPump = async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
                finally
                {
                    inputStopped.TrySetResult();
                }
            }
        };
        var client = CreateClient(sdk, terminal);
        Task<int> execution = client.ExecuteAttachedAsync(
            SandboxId, new MxcExecutionRequest("helper.exe"), CancellationToken.None);

        try
        {
            await inputStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            output.ReleaseRead();
            Assert.Equal(0, await execution.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            output.ReleaseRead();
        }

        Assert.False(output.WasInterrupted);
        Assert.Equal("complete output tail", System.Text.Encoding.UTF8.GetString(terminal.StandardOutput.ToArray()));
        Assert.True(terminal.Input.CanRead);
    }

    [Theory]
    [InlineData("captured")]
    [InlineData("attached")]
    [InlineData("pty")]
    public async Task CancellationClosesAndJoinsRetainedOutputReadersAfterProcessExit(string mode)
    {
        var stdout = new FakeMxcBlockingOutputStream();
        FakeMxcBlockingOutputStream? stderr = mode == "pty"
            ? null
            : new FakeMxcBlockingOutputStream();
        var sdk = new FakeMxcSdkApi();
        FakeMxcProcess? process = null;
        FakeMxcPtySession? pty = null;
        if (mode == "pty")
        {
            pty = new FakeMxcPtySession(completeImmediately: true, outputStream: stdout);
            pty.OutputCloser.OnClose = stdout.ReleaseRead;
            sdk.PtyProcess = pty;
        }
        else
        {
            process = new FakeMxcProcess(
                completeImmediately: true,
                standardOutputStream: stdout,
                standardErrorStream: stderr);
            process.OutputCloser.OnClose = stdout.ReleaseRead;
            process.ErrorCloser.OnClose = stderr!.ReleaseRead;
            sdk.Process = process;
        }

        var terminal = new FakeMxcHostTerminal(interactive: mode == "pty");
        var client = CreateClient(sdk, terminal);
        using var cancellation = new CancellationTokenSource();
        Task execution = mode switch
        {
            "captured" => client.ExecuteAsync(
                SandboxId,
                new MxcExecutionRequest("helper.exe"),
                cancellation.Token),
            "attached" or "pty" => client.ExecuteAttachedAsync(
                SandboxId,
                new MxcExecutionRequest("helper.exe"),
                cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        try
        {
            await stdout.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (stderr is not null)
            {
                await stderr.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.True(process?.TryGetExitCode(out _) ?? pty!.TryGetExitCode(out _));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                execution.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            stdout.ReleaseRead();
            stderr?.ReleaseRead();
        }

        Assert.True(stdout.ReadCompleted.Task.IsCompleted);
        Assert.True(stdout.IsDisposed);
        Assert.False(stdout.WasDisposedWhileReadPending);
        Assert.True(mode == "pty" ? pty!.OutputCloser.Closed : process!.OutputCloser.Closed);
        if (stderr is not null)
        {
            Assert.True(stderr.ReadCompleted.Task.IsCompleted);
            Assert.True(stderr.IsDisposed);
            Assert.False(stderr.WasDisposedWhileReadPending);
            Assert.True(process!.ErrorCloser.Closed);
        }
        if (mode != "captured")
        {
            Assert.True(terminal.Input.CanRead);
        }
        Assert.True(process?.Disposed ?? pty!.Disposed);
    }

    [Theory]
    [InlineData("captured")]
    [InlineData("attached")]
    [InlineData("pty")]
    public async Task OutputFailureClosesSiblingAndJoinsItsReader(string mode)
    {
        FakeMxcBlockingOutputStream? stderr = mode == "pty"
            ? null
            : new FakeMxcBlockingOutputStream();
        var failure = new IOException("fixture output failed");
        var stdout = new FailingReadStream(failure, stderr?.ReadStarted.Task);
        var sdk = new FakeMxcSdkApi();
        FakeMxcProcess? process = null;
        FakeMxcPtySession? pty = null;
        if (mode == "pty")
        {
            pty = new FakeMxcPtySession(
                completeImmediately: false,
                outputStream: stdout);
            sdk.PtyProcess = pty;
        }
        else
        {
            process = new FakeMxcProcess(
                completeImmediately: false,
                standardOutputStream: stdout,
                standardErrorStream: stderr);
            process.ErrorCloser.OnClose = stderr!.ReleaseRead;
            sdk.Process = process;
        }

        var terminal = new FakeMxcHostTerminal(interactive: mode == "pty");
        var client = CreateClient(sdk, terminal);
        Func<Task> execute = mode switch
        {
            "captured" => () => client.ExecuteAsync(
                SandboxId,
                new MxcExecutionRequest("helper.exe"),
                CancellationToken.None),
            "attached" or "pty" => () => client.ExecuteAttachedAsync(
                SandboxId,
                new MxcExecutionRequest("helper.exe"),
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            execute().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(failure, actual);
        Assert.True(process?.Killed ?? pty!.Killed);
        Assert.True(process?.WaitObserved ?? pty!.WaitObserved);
        Assert.True(process?.Disposed ?? pty!.Disposed);
        Assert.True(mode == "pty" ? pty!.OutputCloser.Closed : process!.OutputCloser.Closed);
        if (stderr is not null)
        {
            Assert.True(stderr.ReadStarted.Task.IsCompleted);
            Assert.True(stderr.ReadCompleted.Task.IsCompleted);
            Assert.True(process!.ErrorCloser.Closed);
            Assert.False(stderr.WasDisposedWhileReadPending);
        }
        if (mode != "captured")
        {
            Assert.True(terminal.Input.CanRead);
        }
    }

    [Fact]
    public async Task PumpFailureRemainsPrimaryWhenCancellationArrivesDuringShutdown()
    {
        var stderr = new FakeMxcBlockingOutputStream();
        var failure = new IOException("fixture output failed");
        var stdout = new FailingReadStream(failure, stderr.ReadStarted.Task);
        var process = new FakeMxcProcess(
            completeImmediately: false,
            standardOutputStream: stdout,
            standardErrorStream: stderr);
        var errorCloserClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        process.ErrorCloser.OnClose = () => errorCloserClosed.TrySetResult();
        var sdk = new FakeMxcSdkApi { Process = process };
        var client = CreateClient(sdk);
        using var cancellation = new CancellationTokenSource();
        Task<MxcExecutionResult> execution = client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            cancellation.Token);

        try
        {
            await stderr.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await errorCloserClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(process.Killed);
            cancellation.Cancel();
            stderr.ReleaseRead();

            IOException actual = await Assert.ThrowsAsync<IOException>(() =>
                execution.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Same(failure, actual);
        }
        finally
        {
            stderr.ReleaseRead();
        }
    }

    [Fact]
    public async Task CapturedOutputFailureKillsAndWaitsForTheSpawnedProcess()
    {
        var process = new FakeMxcProcess(
            completeImmediately: false,
            standardOutputStream: new FailingReadStream());
        var sdk = new FakeMxcSdkApi { Process = process };
        var client = CreateClient(sdk);

        await Assert.ThrowsAsync<IOException>(() => client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            CancellationToken.None));

        Assert.True(process.Killed);
        Assert.True(process.WaitObserved);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task PreCanceledExecutionDoesNotSpawnAProcess()
    {
        var sdk = new FakeMxcSdkApi();
        var client = CreateClient(sdk);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            cancellation.Token));

        Assert.Empty(sdk.Calls);
    }

    [Fact]
    public async Task CancellationKillsWaitsAndDisposesOnlyTheSpawnedProcess()
    {
        var process = new FakeMxcProcess(completeImmediately: false);
        var sdk = new FakeMxcSdkApi { Process = process };
        var client = CreateClient(sdk);
        using var cancellation = new CancellationTokenSource();

        Task<MxcExecutionResult> execution = client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            cancellation.Token);
        Assert.Equal("exec", await sdk.DispatchStarted.WaitAsync(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.True(process.Killed);
        Assert.True(process.WaitObserved);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task AttachedRedirectedExecutionRelaysAllThreeStreams()
    {
        var process = new FakeMxcProcess("guest out", "guest err");
        var terminal = new FakeMxcHostTerminal
        {
            Input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("typed input"))
        };
        var sdk = new FakeMxcSdkApi { Process = process };
        var client = CreateClient(sdk, terminal);

        int exitCode = await client.ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("typed input", System.Text.Encoding.UTF8.GetString(
            ((MemoryStream)process.StandardInputStream!).ToArray()));
        Assert.Equal("guest out", System.Text.Encoding.UTF8.GetString(terminal.StandardOutput.ToArray()));
        Assert.Equal("guest err", System.Text.Encoding.UTF8.GetString(terminal.StandardError.ToArray()));
        Assert.True(process.Disposed);
        Assert.False(terminal.EnteredPtyMode);
    }

    [Fact]
    public async Task AttachedExecutionClosesChildStdinAtHostEofWithoutClosingHostInput()
    {
        var hostInput = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("host input"));
        var terminal = new FakeMxcHostTerminal { Input = hostInput };
        var process = new FakeMxcProcess(
            completeImmediately: false,
            completeWhenStandardInputCloses: true);
        var sdk = new FakeMxcSdkApi { Process = process };
        var client = CreateClient(sdk, terminal);

        int exitCode = await client.ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("guest reads stdin to EOF"),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, exitCode);
        Assert.Equal("host input", System.Text.Encoding.UTF8.GetString(
            ((MemoryStream)process.StandardInputStream).ToArray()));
        Assert.True(process.StandardInputStream.IsClosed);
        Assert.True(hostInput.CanRead);
    }

    [Fact]
    public async Task AttachedCancellationClosesChildStdinButLeavesHostInputOwnedByCaller()
    {
        var hostInput = new MemoryStream();
        var terminal = new FakeMxcHostTerminal
        {
            Input = hostInput,
            InputPump = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token)
        };
        var process = new FakeMxcProcess(completeImmediately: false);
        var sdk = new FakeMxcSdkApi { Process = process };
        var client = CreateClient(sdk, terminal);
        using var cancellation = new CancellationTokenSource();

        Task<int> execution = client.ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            cancellation.Token);
        Assert.Equal("exec", await sdk.DispatchStarted.WaitAsync(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.True(process.Killed);
        Assert.True(process.StandardInputStream.IsClosed);
        Assert.True(hostInput.CanRead);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task AttachedInputFailureKillsAndWaitsForTheSpawnedProcess()
    {
        var process = new FakeMxcProcess(completeImmediately: false);
        var terminal = new FakeMxcHostTerminal
        {
            InputPump = (_, _) => Task.FromException(new IOException("fixture input failed"))
        };
        var sdk = new FakeMxcSdkApi { Process = process };
        var client = CreateClient(sdk, terminal);

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
            client.ExecuteAttachedAsync(
                SandboxId,
                new MxcExecutionRequest("helper.exe"),
                CancellationToken.None));

        Assert.Equal("fixture input failed", exception.Message);
        Assert.True(process.Killed);
        Assert.True(process.WaitObserved);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task InteractiveExecutionUsesSdkPtyAndForwardsResizeAndMergedOutput()
    {
        var terminal = new FakeMxcHostTerminal(interactive: true)
        {
            ResizeDuringWatch = new MxcPtySize(40, 120)
        };
        var pty = new FakeMxcPtySession("interactive output", exitCode: 3);
        var sdk = new FakeMxcSdkApi { PtyProcess = pty };
        var client = CreateClient(sdk, terminal);

        int exitCode = await client.ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Equal(["exec-pty"], sdk.Calls);
        Assert.Equal(new MxcPtySize(30, 100), sdk.PtySize);
        Assert.Equal(new MxcPtySize(40, 120), pty.ResizedTo);
        Assert.Equal("interactive output", System.Text.Encoding.UTF8.GetString(
            terminal.StandardOutput.ToArray()));
        Assert.True(terminal.EnteredPtyMode);
        Assert.True(pty.Disposed);
    }

    [Fact]
    public async Task ForegroundAliasStderrProxyDoesNotDisableSdkPty()
    {
        var terminal = new FakeMxcHostTerminal(
            interactive: WindowsMxcHostTerminal.IsPtyAvailable(
                isConsoleInput: true,
                isConsoleOutput: true));
        var sdk = new FakeMxcSdkApi { PtyProcess = new FakeMxcPtySession("interactive output") };
        var client = CreateClient(sdk, terminal);

        await client.ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            CancellationToken.None);

        Assert.Equal(["exec-pty"], sdk.Calls);
        Assert.True(terminal.EnteredPtyMode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RedirectedInputOrOutputKeepsAttachedExecutionOnPipes(
        bool isConsoleInput,
        bool isConsoleOutput)
    {
        var terminal = new FakeMxcHostTerminal(
            interactive: WindowsMxcHostTerminal.IsPtyAvailable(
                isConsoleInput,
                isConsoleOutput));
        var sdk = new FakeMxcSdkApi();
        var client = CreateClient(sdk, terminal);

        await client.ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            CancellationToken.None);

        Assert.Equal(["exec"], sdk.Calls);
        Assert.False(terminal.EnteredPtyMode);
    }

    [Fact]
    public async Task CancellationKillsWaitsAndDisposesInteractivePty()
    {
        var pty = new FakeMxcPtySession(completeImmediately: false);
        var sdk = new FakeMxcSdkApi { PtyProcess = pty };
        var client = CreateClient(sdk, new FakeMxcHostTerminal(interactive: true));
        using var cancellation = new CancellationTokenSource();

        Task<int> execution = client.ExecuteAttachedAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            cancellation.Token);
        Assert.Equal("exec-pty", await sdk.DispatchStarted.WaitAsync(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.True(pty.Killed);
        Assert.True(pty.Disposed);
        Assert.True(pty.WaitObserved);
        Assert.True(Assert.IsType<FakeMxcStreamCloser>(pty.OutputCloser).Closed);
    }

    [Fact]
    public async Task ReadinessProbeUsesOnlySdkLaunchableMethodsForAvailability()
    {
        var sdk = new FakeMxcSdkApi
        {
            Support = new PlatformSupport
            {
                IsSupported = true,
                AvailableMethods = [ContainmentBackend.IsolationSession]
            },
            Backends =
            [
                new AvailableBackend
                {
                    Backend = ContainmentBackend.IsolationSession,
                    Tier = IsolationTier.BaseContainer,
                    Warnings = ["fixture warning"]
                }
            ]
        };
        var client = CreateClient(sdk);

        MxcBackendProbe probe = await client.ProbeBackendAsync(CancellationToken.None);

        Assert.True(probe.IsolationSessionAvailable);
        Assert.Equal(nameof(IsolationTier.BaseContainer), probe.Tier);
        Assert.Equal(["fixture warning"], probe.Warnings);
        Assert.Equal(["probe", "available-backends"], sdk.Calls);
    }

    [Fact]
    public async Task UnsupportedSdkProbeIsAnUnavailableResultNotAProbeFailure()
    {
        var sdk = new FakeMxcSdkApi
        {
            Support = new PlatformSupport
            {
                IsSupported = false,
                Reason = "backend is not launchable",
                AvailableMethods = []
            }
        };
        var client = CreateClient(sdk);

        MxcBackendProbe probe = await client.ProbeBackendAsync(CancellationToken.None);

        Assert.False(probe.IsolationSessionAvailable);
        Assert.Equal(["backend is not launchable"], probe.Warnings);
        Assert.Equal(["probe"], sdk.Calls);
    }

    [Fact]
    public async Task SdkFailureKeepsTypedCodeMessageAndBackendCode()
    {
        var sdk = new FakeMxcSdkApi
        {
            Failure = new SdkMxcException(ErrorCode.StaleId, "session is gone")
        };
        var client = CreateClient(sdk);

        LauncherMxcException exception = await Assert.ThrowsAsync<LauncherMxcException>(() =>
            client.StartAsync(SandboxId, CancellationToken.None));

        Assert.Equal(MxcErrorCode.StaleId, exception.Code);
        Assert.Equal(nameof(ErrorCode.StaleId), exception.BackendCode);
        Assert.Equal("session is gone", exception.Message);
        Assert.IsType<SdkMxcException>(exception.InnerException);
    }

    private static MxcSdkSessionClient CreateClient(
        FakeMxcSdkApi sdk,
        FakeMxcHostTerminal? terminal = null) =>
        new(Runtime, sdk, terminal ?? new FakeMxcHostTerminal());

    private sealed class FailingReadStream : Stream
    {
        private readonly Task? _readGate;

        public FailingReadStream(IOException? failure = null, Task? readGate = null)
        {
            Failure = failure ?? new IOException("fixture output failed");
            _readGate = readGate;
        }

        public IOException Failure { get; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw Failure;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            await FailAsync().ConfigureAwait(false);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => FailAsync();

        private async Task<int> FailAsync()
        {
            if (_readGate is not null)
            {
                await _readGate.ConfigureAwait(false);
            }
            throw Failure;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
