using System.Threading.Channels;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class WindowsMxcConsoleInputReaderTests
{
    [Fact]
    public async Task PreCanceledReaderDoesNotAcquireThreadHandleOrRead()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var api = new FakeConsoleInputApi([0x41]);
        var channel = Channel.CreateUnbounded<byte[]>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsMxcConsoleInputReader.PumpAsync(
                1,
                channel.Writer,
                api,
                cancellation.Token));

        Assert.Equal(0, api.ReadCalls);
        Assert.Equal(0, api.OpenThreadCalls);
    }

    [Fact]
    public async Task ReaderPreservesVtBytesUntilConsoleInputEnds()
    {
        byte[] arrowUp = [0x1b, (byte)'[', (byte)'A'];
        var api = new FakeConsoleInputApi(arrowUp);
        var channel = Channel.CreateUnbounded<byte[]>();

        await WindowsMxcConsoleInputReader.PumpAsync(
            1,
            channel.Writer,
            api,
            CancellationToken.None);

        Assert.Equal(2, api.ReadCalls);
        Assert.True(channel.Reader.TryRead(out byte[]? bytes));
        Assert.Equal(arrowUp, bytes);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CancellationAfterReadinessDoesNotStartAnotherBlockingRead()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeConsoleInputApi([0x41])
        {
            AfterWait = () =>
            {
                _ = cancellation.CancelAsync();
                cancellation.Token.WaitHandle.WaitOne();
            }
        };
        var channel = Channel.CreateUnbounded<byte[]>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsMxcConsoleInputReader.PumpAsync(
                1,
                channel.Writer,
                api,
                cancellation.Token));

        Assert.Equal(0, api.ReadCalls);
    }

    [Fact]
    public async Task CancellationRacingWithSynchronousReadCancelsAndJoinsReader()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeConsoleInputApi([0x41]) { BlockRead = true };
        var channel = Channel.CreateUnbounded<byte[]>();

        Task reader = WindowsMxcConsoleInputReader.PumpAsync(
            1,
            channel.Writer,
            api,
            cancellation.Token);
        await api.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await reader.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, api.ReadCalls);
        Assert.True(api.CancelCalls > 0);
        Assert.True(api.ThreadHandleClosed);
    }

    [Fact]
    public async Task DestinationFailureCancelsAndJoinsConsoleReaderAndSurfacesOriginalError()
    {
        var api = new FakeConsoleInputApi([0x41]) { BlockAfterFirstRead = true };
        var failure = new IOException("guest stdin is closed");
        var destination = new FailAfterReaderBlocksStream(api.ReadStarted.Task, failure);

        IOException observed = await Assert.ThrowsAsync<IOException>(() =>
            WindowsMxcHostTerminal.PumpConsoleInputAsync(
                1,
                destination,
                api,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(failure, observed);
        Assert.Equal(2, api.ReadCalls);
        Assert.True(api.CancelCalls > 0);
        Assert.True(api.ThreadHandleClosed);
    }

    private sealed class FakeConsoleInputApi(byte[] characterBytes) : IMxcConsoleInputApi
    {
        private readonly TaskCompletionSource _releaseRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _finishAfterNextRead;

        public Action? AfterWait { get; init; }
        public bool BlockRead { get; init; }
        public bool BlockAfterFirstRead { get; init; }
        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int OpenThreadCalls { get; private set; }
        public int LastError => 995;
        public uint CurrentThreadId => 1;
        public bool ThreadHandleClosed { get; private set; }

        public nint OpenThreadForSynchronousCancel(uint threadId)
        {
            OpenThreadCalls++;
            return 2;
        }

        public uint WaitForInput(nint handle, uint milliseconds)
        {
            AfterWait?.Invoke();
            return 0;
        }

        public bool TryReadCharacters(nint handle, byte[] buffer, out uint bytesRead)
        {
            ReadCalls++;
            if (BlockRead || (BlockAfterFirstRead && ReadCalls > 1))
            {
                ReadStarted.TrySetResult();
                _releaseRead.Task.GetAwaiter().GetResult();
                bytesRead = 0;
                return false;
            }

            if (_finishAfterNextRead)
            {
                bytesRead = 0;
                return true;
            }
            _finishAfterNextRead = true;
            characterBytes.CopyTo(buffer, 0);
            bytesRead = (uint)characterBytes.Length;
            return true;
        }

        public void CancelSynchronousRead(nint threadHandle)
        {
            CancelCalls++;
            _releaseRead.TrySetResult();
        }

        public void CloseThreadHandle(nint threadHandle) => ThreadHandleClosed = true;
    }

    private sealed class FailAfterReaderBlocksStream(
        Task readStarted,
        IOException failure) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await readStarted.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw failure;
        }
    }
}
