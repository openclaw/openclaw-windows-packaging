using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace OpenClaw.Launcher.Tests;

public sealed class WindowsKillOnCloseJobTests
{
    [Fact]
    public async Task DisposingJobTerminatesAssignedProcess()
    {
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using var stdin = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.None);
        using Process process = job.StartProcess(
            new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                UseShellExecute = false,
                ArgumentList =
                {
                    "/d",
                    "/c",
                    "set",
                    "/p",
                    "line="
                }
            },
            stdin.ClientSafePipeHandle,
            output: null,
            standardError: null);
        stdin.DisposeLocalCopyOfClientHandle();

        Assert.False(process.HasExited);

        job.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);

        Assert.True(process.HasExited);
    }

    [Fact]
    public void TerminateAndWaitReturnsOnlyOnceEveryJobMemberIsGone()
    {
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using Process process = job.StartProcess(
            new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                UseShellExecute = false,
                ArgumentList = { "/d", "/c", "set", "/p", "line=" }
            },
            stdin.ClientSafePipeHandle, output: null, standardError: null);
        stdin.DisposeLocalCopyOfClientHandle();
        Assert.Equal(1, job.ActiveProcessCount);

        job.TerminateAndWait(TimeSpan.FromSeconds(10));

        Assert.Equal(0, job.ActiveProcessCount);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task TerminateAndWaitStopsNestedDescendants()
    {
        string root = TestDirectory.Create();
        string pipeName = "openclaw-job-nested-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            string script = Path.Combine(root, "tree.mjs");
            // The nested child announces itself over a pipe, so the test proceeds only once the tree exists.
            File.WriteAllText(script, $$"""
                import { spawn } from "node:child_process";
                const child = `
                  import { connect } from "node:net";
                  const socket = connect("\\\\\\\\.\\\\pipe\\\\{{pipeName}}");
                  socket.once("connect", () => socket.write(JSON.stringify({ pid: process.pid }) + "\\n"));
                  setInterval(() => {}, 1000);
                `;
                spawn(process.execPath, ["--input-type=module", "-e", child], { stdio: "ignore" });
                setInterval(() => {}, 1000);
                """);
            string node = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim('"'), "node.exe"))
                .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("The fixture needs node.exe on PATH.");
            using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
            using Process parent = job.StartProcess(new ProcessStartInfo
            {
                FileName = node,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { script }
            });
            await pipe.WaitForConnectionAsync(budget.Token);
            using var reader = new StreamReader(pipe);
            using JsonDocument metadata = JsonDocument.Parse((await reader.ReadLineAsync(budget.Token))!);
            using Process nested = Process.GetProcessById(metadata.RootElement.GetProperty("pid").GetInt32());
            Assert.True(job.ActiveProcessCount >= 2);

            job.TerminateAndWait(TimeSpan.FromSeconds(10));

            Assert.Equal(0, job.ActiveProcessCount);
            Assert.True(parent.HasExited);
            Assert.True(nested.HasExited);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public void ANamedJobSomeoneElseAlreadyCreatedIsRefusedInsteadOfAdopted()
    {
        string name = WindowsKillOnCloseJob.GatewayJobName(Environment.ProcessId, DateTime.UtcNow.Ticks);
        using WindowsKillOnCloseJob first = WindowsKillOnCloseJob.Create(name);

        Assert.Throws<System.ComponentModel.Win32Exception>(() => WindowsKillOnCloseJob.Create(name));
        using WindowsKillOnCloseJob? opened = WindowsKillOnCloseJob.TryOpen(name);
        Assert.NotNull(opened);
    }

    [Fact]
    public async Task FastExitRetainsExitCodeAfterDelayedObservation()
    {
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using var stdin = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.None);
        using Process process = job.StartProcess(
            new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                UseShellExecute = false,
                ArgumentList =
                {
                    "/d",
                    "/c",
                    "(set",
                    "/p",
                    "line=)",
                    "&",
                    "exit",
                    "37"
                }
            },
            stdin.ClientSafePipeHandle,
            output: null,
            standardError: null);
        stdin.DisposeLocalCopyOfClientHandle();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // The child waits on stdin while the test opens its watcher handle.
        // Closing this test-owned write end releases the child; then the watcher
        // closes, leaving only StartProcess's retained handle to report its exit code.
        using (Process watcher = Process.GetProcessById(process.Id))
        {
            _ = watcher.SafeHandle;
            stdin.Dispose();
            await watcher.WaitForExitAsync(timeout.Token);
        }

        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(37, process.ExitCode);
    }

    // The child writes to a directory this test creates. "o'connor dir"
    // stands in for a real profile or temporary path that contains spaces and
    // an apostrophe, which PowerShell would otherwise read as syntax.
    [Theory]
    [InlineData("plain")]
    [InlineData("o'connor dir")]
    public async Task StartProcessDeliversArgumentsContainingSpacesAndQuotes(
        string outputDirectoryName)
    {
        string directory = TestDirectory.Create();
        try
        {
            string outputDirectory = Path.Combine(directory, outputDirectoryName);
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(outputDirectory, "argument.txt");
            const string expectedContent = "a b \"quoted\" c";

            using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
            using Process process = job.StartProcess(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.SystemDirectory,
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                UseShellExecute = false,

                // The command text is fixed. The output path reaches the child
                // as environment data instead, because interpolating it here
                // would let an apostrophe in the path terminate the PowerShell
                // literal and fail the run for a reason unrelated to argument
                // marshalling. The command argument still carries the spaces
                // and embedded quotes this test exists to prove survive.
                ArgumentList =
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    "Set-Content -LiteralPath $env:OPENCLAW_TEST_OUTPUT_PATH " +
                        "-Value 'a b \"quoted\" c' -NoNewline"
                },
                Environment = { ["OPENCLAW_TEST_OUTPUT_PATH"] = outputPath }
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            string actualContent =
                await File.ReadAllTextAsync(outputPath, timeout.Token);
            Assert.Equal(expectedContent, actualContent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
