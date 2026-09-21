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

    // The defect this guards: with piped input the pinned backend selects a
    // console-record relay that cannot read a pipe, so the guest saw
    // end-of-input immediately and the caller's bytes were discarded.
    [Fact(Timeout = 300_000)]
    public async Task PipedInputReachesTheExecutorByteForByte()
    {
        string capturedPath = Path.Combine(_root, "captured.bin");
        byte[] payload =
        [
            0x00, 0xFF, 0x0D, 0x0A, 0x1A,
            .. Encoding.UTF8.GetBytes("piped-café-\u00e9\u4e2d\u6587"),
            0x0D, 0x0A, 0x7F,
        ];

        var streams = new FakeStandardStreams(payload);
        var invoker = new ProcessMxcExecutorInvoker(
            new RecordingHostConsole { IsInputRedirected = true },
            streams);

        int exitCode = await invoker.InvokeAttachedAsync(
            CopyStandardInputScript(capturedPath),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Equal(payload, await File.ReadAllBytesAsync(capturedPath).ConfigureAwait(true));
        Assert.Equal("relayed-ok", Encoding.UTF8.GetString(streams.Output.ToArray()));
    }

    // A payload larger than a pipe buffer deadlocks if either direction is
    // copied to completion before the other is drained.
    [Fact(Timeout = 300_000)]
    public async Task PipedInputLargerThanAPipeBufferCompletes()
    {
        string capturedPath = Path.Combine(_root, "large.bin");
        byte[] payload = new byte[1024 * 1024];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        var streams = new FakeStandardStreams(payload);
        var invoker = new ProcessMxcExecutorInvoker(
            new RecordingHostConsole { IsInputRedirected = true },
            streams);

        int exitCode = await invoker.InvokeAttachedAsync(
            CopyStandardInputScript(capturedPath),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Equal(payload, await File.ReadAllBytesAsync(capturedPath).ConfigureAwait(true));
    }

    [Fact(Timeout = 300_000)]
    public async Task RelayedInvocationReportsTheExecutorExitCodeAndErrorOutput()
    {
        var streams = new FakeStandardStreams([]);
        var invoker = new ProcessMxcExecutorInvoker(
            new RecordingHostConsole { IsInputRedirected = true },
            streams);

        int exitCode = await invoker.InvokeAttachedAsync(
            Cmd("echo relayed-failure 1>&2 & exit /b 3"),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(3, exitCode);
        Assert.Contains(
            "relayed-failure",
            Encoding.UTF8.GetString(streams.Error.ToArray()),
            StringComparison.Ordinal);
    }

    // An interactive run must keep inheriting the console handles: that is what
    // lets OpenClaw draw its own prompts and read typed input.
    [Fact(Timeout = 300_000)]
    public async Task AttachedInvocationWithATerminalInputNeverOpensHostStreams()
    {
        var invoker = new ProcessMxcExecutorInvoker(
            new RecordingHostConsole { IsInputRedirected = false },
            new ThrowingStandardStreams());

        int exitCode = await invoker.InvokeAttachedAsync(
            Cmd("exit /b 0"),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
    }

    private MxcExecutorInvocation CopyStandardInputScript(string destinationPath)
    {
        string scriptPath = Path.Combine(
            _root,
            $"copy-stdin-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            scriptPath,
            """
            $ErrorActionPreference = 'Stop'
            $stdin = [Console]::OpenStandardInput()
            $file = [IO.File]::Create($args[0])
            $stdin.CopyTo($file)
            $file.Dispose()
            $out = [Console]::OpenStandardOutput()
            $marker = [Text.Encoding]::UTF8.GetBytes('relayed-ok')
            $out.Write($marker, 0, $marker.Length)
            $out.Flush()
            """);

        return new MxcExecutorInvocation(
            Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath,
             destinationPath]);
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

        public bool IsInputRedirected { get; init; }

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

    // Byte streams the test owns, so the relay can be driven without a real
    // console. The relay closes only the destination it is told to close - the
    // child's input - so these buffers stay readable for the assertions.
    private sealed class FakeStandardStreams(byte[] input) : IHostStandardStreams
    {
        public MemoryStream Output { get; } = new();

        public MemoryStream Error { get; } = new();

        public Stream OpenInput() => new MemoryStream(input, writable: false);

        public Stream OpenOutput() => Output;

        public Stream OpenError() => Error;
    }

    private sealed class ThrowingStandardStreams : IHostStandardStreams
    {
        public Stream OpenInput() => throw new InvalidOperationException(
            "An inherited-handle invocation must not open the host's input.");

        public Stream OpenOutput() => throw new InvalidOperationException(
            "An inherited-handle invocation must not open the host's output.");

        public Stream OpenError() => throw new InvalidOperationException(
            "An inherited-handle invocation must not open the host's error stream.");
    }
}
