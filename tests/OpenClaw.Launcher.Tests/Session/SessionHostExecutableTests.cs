using System.Diagnostics;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Exercises the real <c>openclaw-session-host.exe</c> against real request and
/// result files, rather than only its internals.
/// </summary>
/// <remarks>
/// The helper launches itself, so the test needs no external interpreter and
/// stays deterministic. The outer invocation proves a real shell-free start
/// with an explicit working directory; the inner one deliberately fails so the
/// two control results distinguish "the application exited 64" from "the
/// helper never started it".
/// </remarks>
public sealed class SessionHostExecutableTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    private static string HelperPath
    {
        get
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "openclaw-session-host.exe");
            Assert.True(
                File.Exists(path),
                $"The guest helper was not copied next to the tests: {path}");
            return path;
        }
    }

    /// <summary>
    /// A directory whose name contains a space and literal percent signs, so a
    /// path that was expanded or split would not resolve.
    /// </summary>
    private string CreateHostileDirectory()
    {
        string path = Path.Combine(_testDirectory, "work dir %USERPROFILE%");
        Directory.CreateDirectory(path);
        return path;
    }

    private static int RunHelper(string requestPath, string workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = HelperPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);

        using Process process = Process.Start(startInfo)!;
        process.StandardError.ReadToEnd();
        Assert.True(
            process.WaitForExit(TimeSpan.FromSeconds(30)),
            "The guest helper did not exit.");
        return process.ExitCode;
    }

    private static SessionLaunchResult ReadResult(string requestPath) =>
        SessionLaunchProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(requestPath)));

    [Fact]
    public void TheHelperStartsARealProcessAndDistinguishesItsFailureFromTheChildExitCode()
    {
        string workspace = CreateHostileDirectory();

        // The inner request is deliberately unusable, so the inner helper
        // fails and exits with the helper failure code.
        string innerRequestPath = Path.Combine(workspace, "inner request.json");
        File.WriteAllText(innerRequestPath, "{ not a request");

        string outerRequestPath = Path.Combine(workspace, "outer request.json");
        File.WriteAllText(
            outerRequestPath,
            SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
            {
                RequestId = "outer",
                Executable = HelperPath,
                Arguments = ["--request", innerRequestPath],
                WorkingDirectory = workspace
            }));

        int exitCode = RunHelper(outerRequestPath, AppContext.BaseDirectory);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);

        SessionLaunchResult outer = ReadResult(outerRequestPath);
        Assert.Equal("outer", outer.RequestId);
        Assert.True(outer.Launched);
        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, outer.ExitCode);

        SessionLaunchResult inner = ReadResult(innerRequestPath);
        Assert.False(inner.Launched);
        Assert.Null(inner.ExitCode);
        Assert.NotNull(inner.Error);
    }

    [Fact]
    public void AMissingWorkingDirectoryFailsInsteadOfRunningSomewhereElse()
    {
        // Execution does not inherit the caller's directory, so silently
        // continuing would run the command in the system directory.
        string requestPath = Path.Combine(_testDirectory, "request.json");
        File.WriteAllText(
            requestPath,
            SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
            {
                RequestId = "missing-cwd",
                Executable = HelperPath,
                Arguments = [],
                WorkingDirectory = Path.Combine(_testDirectory, "absent")
            }));

        int exitCode = RunHelper(requestPath, AppContext.BaseDirectory);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);

        SessionLaunchResult result = ReadResult(requestPath);
        Assert.False(result.Launched);
        Assert.Contains("working directory", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRequestFileIsRecordedInTheControlResult()
    {
        string requestPath = Path.Combine(_testDirectory, "absent.json");

        int exitCode = RunHelper(requestPath, AppContext.BaseDirectory);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.True(File.Exists(SessionLaunchProtocol.ResultPathFor(requestPath)));

        SessionLaunchResult result = ReadResult(requestPath);
        Assert.False(result.Launched);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
