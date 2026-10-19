namespace OpenClaw.Launcher.Tests;

public sealed class WindowsMxcHostStdioTests
{
    [Fact]
    public async Task ConsoleReadCancellationDuringShutdownJoinsAndClosesInput()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CancelledReadStream(cancellation.Cancel);
        var destination = new ClosingStream();
        var stdio = new WindowsMxcHostStdio(openInput: () => source);

        await stdio.CopyInputAsync(destination, cancellation.Token);

        Assert.True(source.Closed);
        Assert.True(destination.Closed);
    }

    [Fact]
    public async Task UnrequestedConsoleReadCancellationRemainsAFailure()
    {
        var source = new CancelledReadStream(() => { });
        var destination = new ClosingStream();
        var stdio = new WindowsMxcHostStdio(openInput: () => source);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stdio.CopyInputAsync(destination, CancellationToken.None));

        Assert.True(source.Closed);
        Assert.True(destination.Closed);
    }

    private sealed class CancelledReadStream(Action beforeRead) : ClosingStream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            beforeRead();
            throw new OperationCanceledException("The console read was canceled.");
        }
    }

    private class ClosingStream : MemoryStream
    {
        public bool Closed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }
}
