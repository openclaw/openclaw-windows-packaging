using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.StateTransfer;

public sealed class StateTransferProcessTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task CaptureStopsDescendantsBeforeReturningAndDrainsInheritedOutput()
    {
        string name = "openclaw-capture-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        const string script = """
            import { spawn } from "node:child_process";
            const childProgram = `
              import { connect } from "node:net";
              const socket = connect("\\\\\\\\.\\\\pipe\\\\" + process.env.STATE_TEST_PIPE);
              socket.once("connect", () => socket.write(JSON.stringify({ pid: process.pid }) + "\\n"));
              socket.once("data", () => process.send("ready"));
            `;
            const child = spawn(process.execPath, ["--input-type=module", "-e", childProgram],
              { stdio: ["ignore", "inherit", "inherit", "ipc"] });
            child.once("message", () => {
              console.log("parent finished");
              process.exit(0);
            });
            child.once("exit", (code) => process.exit(code || 1));
            """;
        Task<SessionProcessOutput> capture = Task.Run(() => SessionProcessLauncher.Capture(
            StateTransferProcessFixture.Request(_root, script) with
            {
                Environment = new Dictionary<string, string>
                {
                    ["NODE_OPTIONS"] = "",
                    ["STATE_TEST_PIPE"] = name
                }
            }, TimeSpan.FromSeconds(15)));
        await pipe.WaitForConnectionAsync(budget.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        string identity = (await reader.ReadLineAsync(budget.Token))!;
        using JsonDocument metadata = JsonDocument.Parse(identity);
        using Process descendant = Process.GetProcessById(metadata.RootElement.GetProperty("pid").GetInt32());
        try
        {
            Assert.False(descendant.HasExited);
            await writer.WriteLineAsync("continue".AsMemory(), budget.Token);

            SessionProcessOutput result = await capture.WaitAsync(budget.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("parent finished", result.StandardOutput.Trim());
            Assert.Empty(result.StandardError);
            await descendant.WaitForExitAsync(budget.Token);
        }
        finally
        {
            StateTransferProcessFixture.Stop(descendant);
            await capture.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task InterruptedWorkerReleasesItsKernelLeaseWithoutRemovingTheLockFile()
    {
        string profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profile);
        string directory = Path.Combine(_root, "transfer");
        Directory.CreateDirectory(directory);
        string lockPath = Path.Combine(directory, "worker.lock");
        string path = Convert.ToBase64String(Encoding.UTF8.GetBytes(lockPath));
        string script = $$"""
            $path = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{path}}'))
            $lease = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            [Console]::Out.WriteLine('locked')
            [Console]::Out.Flush()
            $null = [Console]::In.ReadLine()
            $lease.Dispose()
            """;
        ProcessStartInfo start = new(StateTransferProcessFixture.Executable("pwsh.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using Process worker = Process.Start(start)!;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transfer = new SessionStateTransfer(profile, directory, new SessionStateTransferTests.ArchiveApplication());
        SessionStateTransferRequest request = new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Action = SessionStateTransferAction.Capture,
            WorkspaceDirectory = _root
        };
        try
        {
            Assert.Equal("locked", await worker.StandardOutput.ReadLineAsync(budget.Token));
            Assert.Throws<SessionLaunchException>(() => transfer.Execute(request));
            Assert.Empty(Directory.GetFiles(_root, "*.tar.gz"));

            StateTransferProcessFixture.Stop(worker);

            SessionStateTransferResult captured = transfer.Execute(request);
            Assert.True(captured.Archive!.Verified);
            Assert.True(File.Exists(lockPath));
            Assert.Equal(SessionStateTransferTests.ArchiveApplication.Bytes,
                File.ReadAllBytes(captured.Archive.Path));
        }
        finally
        {
            StateTransferProcessFixture.Stop(worker);
        }
    }
}

internal static class StateTransferProcessFixture
{
    public static string Executable(string name)
    {
        string? path = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), name))
            .FirstOrDefault(File.Exists);
        Assert.True(path is not null, $"These integration scenarios require {name} on PATH.");
        return path;
    }

    public static SessionLaunchRequest Request(string root, string script, params string[] arguments) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Executable = Executable("node.exe"),
        Arguments = ["--no-warnings", "--input-type=module", "-e", script, .. arguments],
        WorkingDirectory = root,
        Environment = new Dictionary<string, string> { ["NODE_OPTIONS"] = "" }
    };

    public static Process StartNode(SessionLaunchRequest request)
    {
        ProcessStartInfo start = new(request.Executable!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in request.Arguments!)
        {
            start.ArgumentList.Add(argument);
        }
        foreach ((string name, string value) in request.Environment!)
        {
            start.Environment[name] = value;
        }
        return Process.Start(start) ?? throw new InvalidOperationException("The fixture Node.js process did not start.");
    }

    public static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }
}
