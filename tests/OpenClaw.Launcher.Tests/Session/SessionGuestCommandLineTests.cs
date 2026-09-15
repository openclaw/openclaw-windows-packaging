using System.Diagnostics;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Runs the command line this package hands the backend through the command
/// processor the backend actually dispatches with.
/// </summary>
/// <remarks>
/// <para>
/// The defect this covers reached a real installation: every path was quoted,
/// the string looked correct, and the launch still failed with
/// <c>'C:\Program' is not recognized</c>. Asserting on the composed string
/// would have passed, because the string was never the problem; the command
/// processor's treatment of it was. These tests therefore execute it.
/// </para>
/// <para>
/// The packaged helper lives under <c>C:\Program Files\WindowsApps\...</c>, so
/// a space in the executable path is the normal case rather than an edge one.
/// </para>
/// </remarks>
public sealed class SessionGuestCommandLineTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Writes a script that reports the argument vector it actually received.
    /// </summary>
    private string CreateProbe(string directoryName)
    {
        string directory = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(directory);
        string probe = Path.Combine(directory, "probe.cmd");
        File.WriteAllText(
            probe,
            "@echo off\r\n" +
            "echo STARTED\r\n" +
            "echo OPTION=%~1\r\n" +
            "echo REQUEST=%~2\r\n",
            System.Text.Encoding.ASCII);
        return probe;
    }

    /// <summary>
    /// Dispatches exactly as the pinned runtime does: the command line is
    /// appended to <c>cmd.exe /c</c> verbatim, with no further quoting.
    /// </summary>
    private static (int ExitCode, string Output, string Error) Dispatch(string commandLine)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe"),

            // Arguments, not ArgumentList: the point is to reproduce a raw
            // command line rather than let .NET re-quote it.
            Arguments = "/c " + commandLine,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));
        return (process.ExitCode, output, error);
    }

    [Fact]
    public void TheHelperRunsWhenItsDirectoryContainsASpace()
    {
        string probe = CreateProbe("Program Files");
        string request = Path.Combine(_root, "Program Files", "launch.json");
        File.WriteAllText(request, "{}");

        (int exitCode, string output, string error) = Dispatch(
            SessionExecutor.BuildGuestCommandLine(probe, request));

        // The measured failure was exit 1 with 'C:\Program' is not recognized.
        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Contains("STARTED", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRequestPathArrivesWholeWhenItContainsASpace()
    {
        string probe = CreateProbe("Program Files");
        string request = Path.Combine(_root, "Program Files", "launch request.json");
        File.WriteAllText(request, "{}");

        (int exitCode, string output, _) = Dispatch(
            SessionExecutor.BuildGuestCommandLine(probe, request));

        Assert.Equal(0, exitCode);

        // A truncated path here would send the helper looking for a request
        // file that does not exist, which surfaces much later as "the session
        // did not report a launch result".
        Assert.Contains($"REQUEST={request}", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionIsNotAbsorbedIntoTheExecutablePath()
    {
        string probe = CreateProbe("Program Files");
        string request = Path.Combine(_root, "Program Files", "launch.json");
        File.WriteAllText(request, "{}");

        (_, string output, _) = Dispatch(
            SessionExecutor.BuildGuestCommandLine(probe, request));

        Assert.Contains("OPTION=--request", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--request")]
    [InlineData("--inspect")]
    [InlineData("--stop")]
    public void EveryHelperModeSurvivesDispatch(string option)
    {
        // The gateway drives the same helper through inspect and stop, so a
        // fix applied only to the launch path would leave those broken.
        string probe = CreateProbe("Program Files");
        string request = Path.Combine(_root, "Program Files", "launch.json");
        File.WriteAllText(request, "{}");

        (int exitCode, string output, _) = Dispatch(
            SessionExecutor.BuildGuestCommandLine(probe, request, option));

        Assert.Equal(0, exitCode);
        Assert.Contains($"OPTION={option}", output, StringComparison.Ordinal);
    }

    [Fact]
    public void APathWithoutSpacesStillRunsAndKeepsItsArguments()
    {
        // The outer pair must not break the ordinary case it was added for the
        // sake of the harder one.
        string probe = CreateProbe("plain");
        string request = Path.Combine(_root, "plain", "launch.json");
        File.WriteAllText(request, "{}");

        (int exitCode, string output, _) = Dispatch(
            SessionExecutor.BuildGuestCommandLine(probe, request));

        Assert.Equal(0, exitCode);
        Assert.Contains($"REQUEST={request}", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("has\"quote")]
    [InlineData("has%percent%")]
    public void APathTheCommandProcessorCannotCarryIsRefused(string fragment)
    {
        // Refusing is the point: cmd.exe expands %VAR% and lets a quote
        // truncate the rest of the line, and neither can be quoted around.
        Assert.Throws<SessionException>(
            () => SessionExecutor.BuildGuestCommandLine(
                Path.Combine(_root, fragment, "probe.exe"),
                Path.Combine(_root, "launch.json")));

        Assert.Throws<SessionException>(
            () => SessionExecutor.BuildGuestCommandLine(
                Path.Combine(_root, "probe.exe"),
                Path.Combine(_root, fragment, "launch.json")));
    }
}
