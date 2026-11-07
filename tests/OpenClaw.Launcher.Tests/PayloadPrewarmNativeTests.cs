using System.Diagnostics;
using System.IO.Pipes;

namespace OpenClaw.Launcher.Tests;

public sealed class PayloadPrewarmNativeTests
{
    // A caller such as Companion waits for EOF on setup's redirected output.
    // The write end is inheritable, as a redirected standard handle is; if the
    // detached worker inherited it, EOF would wait for the worker to exit.
    [Fact]
    public async Task ADetachedWorkerDoesNotHoldTheStartersInheritableOutputOpen()
    {
        using AnonymousPipeServerStream reader = new(PipeDirection.In, HandleInheritability.Inheritable);
        AnonymousPipeClientStream writer = new(PipeDirection.Out, reader.ClientSafePipeHandle);
        int workerId = 0;
        try
        {
            workerId = PayloadPrewarmNative.StartDetached(
                Path.Combine(Environment.SystemDirectory, "PING.EXE"),
                "-n 120 127.0.0.1");

            writer.Write("{\"ok\":true}"u8);
            writer.Dispose();
            reader.DisposeLocalCopyOfClientHandle();

            Task<string> output = Task.Run(() =>
            {
                using StreamReader text = new(reader);
                return text.ReadToEnd();
            });

            // A blocked read never observes cancellation, so the wait is
            // bounded outside it; killing the worker below releases the read.
            string text = await output.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("{\"ok\":true}", text);
            using Process worker = Process.GetProcessById(workerId);
            Assert.False(worker.HasExited);
        }
        finally
        {
            writer.Dispose();
            if (workerId != 0)
            {
                try
                {
                    using Process worker = Process.GetProcessById(workerId);
                    if (string.Equals(worker.ProcessName, "PING", StringComparison.OrdinalIgnoreCase))
                    {
                        worker.Kill();
                        _ = worker.WaitForExit(TimeSpan.FromSeconds(30));
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }
    }
}
