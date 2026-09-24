using System.Diagnostics;
using System.IO.Pipes;

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
    public async Task FastExitRetainsExitCodeAfterDelayedObservation()
    {
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using var stdout = new AnonymousPipeServerStream(
            PipeDirection.In,
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
                    "exit",
                    "37"
                }
            },
            input: null,
            output: stdout.ClientSafePipeHandle,
            standardError: null);
        stdout.DisposeLocalCopyOfClientHandle();

        // EOF means the child has exited: its handle table held the only write
        // end. The test never opens its own handle to the child, so only the
        // handle StartProcess retained can still report the exit code.
        await stdout.CopyToAsync(Stream.Null);
        await process.WaitForExitAsync();

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
