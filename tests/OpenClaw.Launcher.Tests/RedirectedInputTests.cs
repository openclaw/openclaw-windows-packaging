using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text;

namespace OpenClaw.Launcher.Tests;

public sealed class RedirectedInputTests
{
    [Fact(Timeout = 5_000)]
    [SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using IDisposable instances complete",
        Justification =
            "The regression requires the pipe read to remain pending after the canceled wrapper " +
            "returns; closing the writer at scope exit releases the owned reader continuation.")]
    public async Task CancellationDoesNotWaitForAnOpenRedirectedPipe()
    {
        using var writer = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.None);
        var reader = new AnonymousPipeClientStream(
            PipeDirection.In,
            writer.GetClientHandleAsString());
        using var cancellation = new CancellationTokenSource();

        Task<string?> pending = Program.ReadInputLineAsync(
            reader,
            Encoding.UTF8,
            cancellation.Token);
        Assert.False(pending.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
