using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

internal sealed class FakeMxcSdkApi : IMxcSdkApi
{
    private readonly TaskCompletionSource<string> _dispatchStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public string NativeVersion { get; set; } = "fixture";
    public PlatformSupport Support { get; set; } = new() { IsSupported = true };
    public IReadOnlyList<AvailableBackend> Backends { get; set; } = [];
    public Exception? Failure { get; set; }
    public Queue<Exception> StartFailures { get; } = new();
    public int StartsBeforeFailure { get; set; }
    private int _startCount;
    public ProvisionRequest? ProvisionRequest { get; private set; }
    public ContainerId? LastContainerId { get; private set; }
    public List<string> Calls { get; } = [];
    public Task<string> DispatchStarted => _dispatchStarted.Task;
    public ProvisionResult ProvisionResult { get; set; } = new()
    {
        ContainerId = new ContainerId("iso:minted"),
        Metadata = new IsolationSessionProvisionMetadata
        {
            AgentUserName = "agent_fixture",
            AgentUserSid = "S-1-5-21-0-0-0-1234",
            EphemeralWorkspacePath = @"C:\fixture\workspace"
        }
    };
    public FakeMxcProcess Process { get; set; } = new();
    public FakeMxcPtySession PtyProcess { get; set; } = new();
    public MxcPtySize? PtySize { get; private set; }

    public PlatformSupport GetPlatformSupport()
    {
        ThrowIfFailed();
        RecordCall("probe");
        return Support;
    }

    public IReadOnlyList<AvailableBackend> GetAvailableBackends()
    {
        ThrowIfFailed();
        RecordCall("available-backends");
        return Backends;
    }

    public ProvisionResult ProvisionContainer(ProvisionRequest request)
    {
        ThrowIfFailed();
        RecordCall("provision");
        ProvisionRequest = request;
        return ProvisionResult;
    }

    public LifecycleResult StartContainer(ContainerId id)
    {
        RecordCall("start");
        LastContainerId = id;
        ThrowIfFailed();
        if (_startCount++ >= StartsBeforeFailure && StartFailures.TryDequeue(out Exception? failure))
        {
            throw failure;
        }
        return new LifecycleResult();
    }

    public IMxcProcess SpawnInContainer(ContainerId id, ExecutionRequest request)
    {
        ThrowIfFailed();
        RecordCall("exec");
        LastContainerId = id;
        LastExecutionRequest = request;
        return Process;
    }

    public IMxcPtySession SpawnInContainerWithPty(
        ContainerId id,
        ExecutionRequest request,
        MxcPtySize size)
    {
        ThrowIfFailed();
        RecordCall("exec-pty");
        LastContainerId = id;
        LastExecutionRequest = request;
        PtySize = size;
        return PtyProcess;
    }

    public ExecutionRequest? LastExecutionRequest { get; private set; }

    public LifecycleResult StopContainer(ContainerId id)
    {
        ThrowIfFailed();
        RecordCall("stop");
        LastContainerId = id;
        return new LifecycleResult();
    }

    public LifecycleResult DeprovisionContainer(ContainerId id)
    {
        ThrowIfFailed();
        RecordCall("deprovision");
        LastContainerId = id;
        return new LifecycleResult();
    }

    private void ThrowIfFailed()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }

    private void RecordCall(string call)
    {
        Calls.Add(call);
        _dispatchStarted.TrySetResult(call);
    }
}

internal sealed class FakeMxcProcess : IMxcProcess
{
    private readonly TaskCompletionSource<WaitResult> _exit = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeMxcProcess(
        string standardOutput = "",
        string standardError = "",
        int exitCode = 0,
        bool completeImmediately = true,
        Stream? standardOutputStream = null,
        Stream? standardErrorStream = null,
        bool completeWhenStandardInputCloses = false)
    {
        StandardInputStream = new FakeMxcInputStream(() =>
        {
            if (completeWhenStandardInputCloses)
            {
                Complete(exitCode);
            }
        });
        StandardOutputStream = standardOutputStream ?? new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(standardOutput));
        StandardErrorStream = standardErrorStream ?? new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(standardError));
        ExitCode = exitCode;
        if (completeImmediately)
        {
            _exit.TrySetResult(new WaitResult { ExitCode = exitCode });
        }
    }

    public FakeMxcInputStream StandardInputStream { get; }
    public Stream? StandardOutputStream { get; }
    public Stream? StandardErrorStream { get; }
    public int ExitCode { get; private set; }
    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }
    public bool WaitObserved { get; private set; }
    public FakeMxcStreamCloser OutputCloser { get; } = new();
    public FakeMxcStreamCloser ErrorCloser { get; } = new();
    public uint Id => 1;
    public Stream? StandardInput => StandardInputStream;
    public Stream? StandardOutput => StandardOutputStream;
    public Stream? StandardError => StandardErrorStream;
    public IMxcStreamCloser? StandardOutputCloser => OutputCloser;
    public IMxcStreamCloser? StandardErrorCloser => ErrorCloser;
    public IReadOnlyList<string> Warnings => [];
    public ExecutionMetadata? OutputMetadata => null;

    public WaitResult Wait() => _exit.Task.GetAwaiter().GetResult();

    public Task<WaitResult> WaitAsync(CancellationToken cancellationToken = default) =>
        WaitCoreAsync(cancellationToken);

    private async Task<WaitResult> WaitCoreAsync(CancellationToken cancellationToken)
    {
        WaitObserved = true;
        return await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetExitCode(out int exitCode)
    {
        if (_exit.Task.IsCompletedSuccessfully)
        {
            exitCode = _exit.Task.Result.ExitCode;
            return true;
        }

        exitCode = 0;
        return false;
    }

    public async Task<(WaitResult Result, byte[] Stdout, byte[] Stderr)> WaitForExitWithOutputAsync(
        CancellationToken cancellationToken = default)
    {
        WaitResult result = await WaitAsync(cancellationToken).ConfigureAwait(false);
        return (result, [], []);
    }

    public void Kill()
    {
        Killed = true;
        ExitCode = 137;
        _exit.TrySetResult(new WaitResult { ExitCode = ExitCode });
    }

    public void Complete(int exitCode)
    {
        ExitCode = exitCode;
        _exit.TrySetResult(new WaitResult { ExitCode = exitCode });
    }

    public void Dispose()
    {
        Disposed = true;
        StandardInputStream?.Dispose();
        StandardOutputStream?.Dispose();
        StandardErrorStream?.Dispose();
    }
}

internal sealed class FakeMxcPtySession : IMxcPtySession
{
    private readonly FakeMxcProcess _process;

    public FakeMxcPtySession(
        string output = "",
        int exitCode = 0,
        bool completeImmediately = true,
        Stream? outputStream = null)
    {
        _process = new FakeMxcProcess(
            output,
            exitCode: exitCode,
            completeImmediately: completeImmediately,
            standardOutputStream: outputStream);
    }

    public Stream Input => _process.StandardInputStream!;
    public Stream Output => _process.StandardOutputStream!;
    public FakeMxcStreamCloser OutputCloser => _process.OutputCloser;
    IMxcStreamCloser? IMxcPtySession.OutputCloser => OutputCloser;
    public bool Killed => _process.Killed;
    public bool Disposed => _process.Disposed;
    public bool WaitObserved => _process.WaitObserved;
    public MxcPtySize? ResizedTo { get; private set; }
    public Task<WaitResult> WaitAsync(CancellationToken cancellationToken) =>
        _process.WaitAsync(cancellationToken);
    public bool TryGetExitCode(out int exitCode) => _process.TryGetExitCode(out exitCode);
    public void Kill() => _process.Kill();
    public void Resize(MxcPtySize size) => ResizedTo = size;
    public void Dispose() => _process.Dispose();
}

internal sealed class FakeMxcStreamCloser : IMxcStreamCloser
{
    public bool Closed { get; private set; }
    public Action? OnClose { get; set; }

    public void Close()
    {
        Closed = true;
        OnClose?.Invoke();
    }

    public void Dispose() => Closed = true;
}

internal sealed class FakeMxcBlockingOutputStream(string output = "") : Stream
{
    private readonly byte[] _output = System.Text.Encoding.UTF8.GetBytes(output);
    private int _offset;
    private readonly TaskCompletionSource _releaseRead = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeReads;

    public TaskCompletionSource ReadStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReadCompleted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsDisposed { get; private set; }
    public bool WasInterrupted { get; private set; }
    public bool WasDisposedWhileReadPending { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void ReleaseRead() => _releaseRead.TrySetResult();
    public void InterruptRead()
    {
        WasInterrupted = true;
        ReleaseRead();
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        BeginRead();
        try
        {
            _releaseRead.Task.GetAwaiter().GetResult();
            return CopyOutput(buffer.AsSpan(offset, count));
        }
        finally
        {
            EndRead();
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        BeginRead();
        try
        {
            await _releaseRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return CopyOutput(buffer.Span);
        }
        finally
        {
            EndRead();
        }
    }

    private int CopyOutput(Span<byte> buffer)
    {
        if (WasInterrupted)
        {
            return 0;
        }
        int count = Math.Min(buffer.Length, _output.Length - _offset);
        _output.AsSpan(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    private void BeginRead()
    {
        Interlocked.Increment(ref _activeReads);
        ReadStarted.TrySetResult();
    }

    private void EndRead()
    {
        Interlocked.Decrement(ref _activeReads);
        ReadCompleted.TrySetResult();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WasDisposedWhileReadPending = Volatile.Read(ref _activeReads) != 0;
            IsDisposed = true;
        }
        base.Dispose(disposing);
    }
}

internal sealed class FakeMxcInputStream(Action? onClose = null) : MemoryStream
{
    public bool IsClosed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsClosed)
        {
            IsClosed = true;
            onClose?.Invoke();
        }
        base.Dispose(disposing);
    }
}

internal sealed class FakeMxcHostTerminal(bool interactive = false) : IMxcHostTerminal
{
    public bool IsInteractive { get; } = interactive;
    public MxcPtySize Size { get; set; } = new(30, 100);
    public Stream Input { get; set; } = new MemoryStream();
    public MemoryStream StandardOutput { get; } = new();
    public MemoryStream StandardError { get; } = new();
    public MxcPtySize? ResizeDuringWatch { get; set; }
    public Func<Stream, CancellationToken, Task>? InputPump { get; set; }
    public bool EnteredPtyMode { get; private set; }

    public IDisposable EnterPtyMode()
    {
        EnteredPtyMode = true;
        return new FakeScope();
    }

    public MxcPtySize GetSize() => Size;

    public Task PumpInputAsync(Stream destination, CancellationToken cancellationToken) =>
        InputPump is { } inputPump
            ? inputPump(destination, cancellationToken)
            : Input.CopyToAsync(destination, 81920, cancellationToken);

    public async Task PumpOutputAsync(
        Stream source,
        bool standardError,
        CancellationToken cancellationToken)
    {
        Stream destination = standardError ? StandardError : StandardOutput;
        await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
    }

    public async Task WatchResizeAsync(
        Action<MxcPtySize> resize,
        CancellationToken cancellationToken)
    {
        if (ResizeDuringWatch is { } changed)
        {
            resize(changed);
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private sealed class FakeScope : IDisposable
    {
        public void Dispose() { }
    }
}
