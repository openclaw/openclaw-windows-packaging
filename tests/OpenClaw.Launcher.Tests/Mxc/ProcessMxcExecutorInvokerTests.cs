using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class ProcessMxcExecutorInvokerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public async Task AttachedInvocationInitializesUtf8InsideConsoleCapture()
    {
        var console = new RecordingHostConsole();
        var invoker = new ProcessMxcExecutorInvoker(console);

        int exitCode = await invoker.InvokeAttachedAsync(
            Cmd("exit /b 0"),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["capture", "utf8", "restore"], console.Events);
    }

    [Fact]
    public async Task BufferedInvocationClosesStandardInput()
    {
        var invoker = new ProcessMxcExecutorInvoker();

        MxcExecutorOutcome outcome = await invoker.InvokeAsync(
            Cmd("set /p value= & echo eof"),
            CancellationToken.None);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("eof", outcome.StandardOutput, StringComparison.Ordinal);
    }

    // The deadline is a hang detector, not a synchronization budget. The wait
    // below completes as soon as the executor connects or exits, so a slow
    // cold start cannot reach it; only a genuine hang can.
    [Fact(Timeout = 300_000)]
    public async Task CancellationTerminatesBufferedExecutorBeforeReturning()
    {
        string scriptPath = Path.Combine(_root, "wait.ps1");
        string pipeName = $"OpenClaw-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(
            scriptPath,
            """
            $pipe = [IO.Pipes.NamedPipeClientStream]::new(
                '.', $args[0], [IO.Pipes.PipeDirection]::InOut)
            $pipe.Connect()
            $writer = [IO.StreamWriter]::new($pipe)
            $writer.AutoFlush = $true
            $writer.WriteLine($PID)
            [void]$pipe.ReadByte()
            """);

        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource();
        var invoker = new ProcessMxcExecutorInvoker();
        string powershell = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Task<MxcExecutorOutcome> invocation = invoker.InvokeAsync(
            new MxcExecutorInvocation(
                powershell,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath,
                 pipeName]),
            cancellation.Token);

        Task connection = pipe.WaitForConnectionAsync(CancellationToken.None);
        Task completed = await Task.WhenAny(connection, invocation)
            .ConfigureAwait(true);
        if (completed == invocation)
        {
            MxcExecutorOutcome outcome = await invocation.ConfigureAwait(true);
            throw new Xunit.Sdk.XunitException(
                "Executor exited before connecting to the named pipe " +
                $"(exit code {outcome.ExitCode}). Standard output: " +
                $"{outcome.StandardOutput} Standard error: {outcome.StandardError}");
        }

        await connection.ConfigureAwait(true);
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        string processIdText = await reader.ReadLineAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Executor did not report its process ID.");
        int processId = int.Parse(
            processIdText,
            System.Globalization.CultureInfo.InvariantCulture);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }

    [Fact]
    public async Task PreCancelledInvocationDoesNotSpawnExecutor()
    {
        string markerPath = Path.Combine(_root, "spawned.txt");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoker = new ProcessMxcExecutorInvoker();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.InvokeAsync(
                Cmd($"echo spawned>\"{markerPath}\""),
                cancellation.Token));

        Assert.False(File.Exists(markerPath));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static MxcExecutorInvocation Cmd(string command) =>
        new(
            Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", command]);

    private sealed class RecordingHostConsole : IHostConsole
    {
        public List<string> Events { get; } = [];

        public bool IsInteractive => true;

        public IDisposable Capture(Action<string> log)
        {
            Events.Add("capture");
            return new Restore(Events);
        }

        public void InitializeUtf8() => Events.Add("utf8");

        private sealed class Restore(List<string> events) : IDisposable
        {
            public void Dispose() => events.Add("restore");
        }
    }
}
