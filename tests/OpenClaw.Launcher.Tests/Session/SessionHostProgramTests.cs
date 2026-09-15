using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;
using SessionHostProgram = OpenClaw.SessionHost.Program;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionHostProgramTests
{
    private sealed class RecordingLauncher(int exitCode) : ISessionProcessLauncher
    {
        public SessionLaunchRequest? Request { get; private set; }

        public SessionLaunchRequest? DetachedRequest { get; private set; }

        public string? HelperPath { get; private set; }

        public SessionDetachedProcess Detached { get; set; } =
            new(4321, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        public int Run(SessionLaunchRequest request)
        {
            Request = request;
            return exitCode;
        }

        public SessionDetachedProcess Start(SessionLaunchRequest request, string helperPath)
        {
            DetachedRequest = request;
            HelperPath = helperPath;
            return Detached;
        }
    }

    private sealed class ThrowingLauncher(string message) : ISessionProcessLauncher
    {
        public int Run(SessionLaunchRequest request) =>
            throw new SessionLaunchException(message);

        public SessionDetachedProcess Start(SessionLaunchRequest request, string helperPath) =>
            throw new SessionLaunchException(message);
    }

    private sealed class ResultRecorder
    {
        public string? Path { get; private set; }

        public SessionLaunchResult? Result { get; private set; }

        public void Write(string path, SessionLaunchResult result)
        {
            Path = path;
            Result = result;
        }
    }

    private static string RequestJson(
        IReadOnlyList<string>? arguments = null,
        string requestId = "r-1") =>
        SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
        {
            RequestId = requestId,
            Executable = @"C:\node\node.exe",
            Arguments = arguments ?? [@"C:\app\openclaw.mjs"],
            WorkingDirectory = @"C:\work"
        });

    [Fact]
    public void TheArgumentVectorReachesTheLauncherUnchanged()
    {
        string[] arguments = [@"C:\app\openclaw.mjs", "%USERPROFILE%", "q\"x", string.Empty];
        var launcher = new RecordingLauncher(0);
        var errors = new StringWriter();
        var results = new ResultRecorder();

        int exitCode = SessionHostProgram.Run(
            ["--request", @"C:\ws\req.json"],
            launcher,
            errors,
            _ => RequestJson(arguments),
            results.Write);

        Assert.Equal(0, exitCode);
        Assert.Equal(arguments, launcher.Request!.Arguments);
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public void AVersionOneRequestWithoutAModeRunsAttached()
    {
        var launcher = new RecordingLauncher(42);
        var results = new ResultRecorder();

        int exitCode = SessionHostProgram.Run(
            ["--request", @"C:\ws\req.json"],
            launcher,
            new StringWriter(),
            _ =>
                """
                {
                  "schemaVersion": 1,
                  "requestId": "r-1",
                  "executable": "C:\\node\\node.exe",
                  "arguments": ["C:\\app\\openclaw.mjs"],
                  "workingDirectory": "C:\\work"
                }
                """,
            results.Write);

        Assert.Equal(42, exitCode);
        Assert.Equal(SessionLaunchMode.Attached, launcher.Request!.Mode);
        Assert.Null(launcher.DetachedRequest);
        Assert.True(results.Result!.Launched);
        Assert.Equal(42, results.Result.ExitCode);
    }

    [Fact]
    public void TheApplicationExitCodeIsPropagatedAndRecordedAsLaunched()
    {
        var results = new ResultRecorder();

        int exitCode = SessionHostProgram.Run(
            ["--request", @"C:\ws\req.json"],
            new RecordingLauncher(42),
            new StringWriter(),
            _ => RequestJson(),
            results.Write);

        Assert.Equal(42, exitCode);
        Assert.Equal(@"C:\ws\req.json.result.json", results.Path);
        Assert.True(results.Result!.Launched);
        Assert.Equal(42, results.Result.ExitCode);
        Assert.Null(results.Result.Error);
    }

    [Fact]
    public void AnApplicationExitCodeMatchingTheHelperFailureCodeIsStillLaunched()
    {
        // The decisive ambiguity: without the control result, OpenClaw exiting
        // 64 would be indistinguishable from the helper failing to start it.
        var results = new ResultRecorder();

        int exitCode = SessionHostProgram.Run(
            ["--request", @"C:\ws\req.json"],
            new RecordingLauncher(SessionLaunchProtocol.HelperFailureExitCode),
            new StringWriter(),
            _ => RequestJson(),
            results.Write);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.True(results.Result!.Launched);
    }

    [Fact]
    public void AFailedLaunchIsRecordedAsNotLaunchedWithItsReason()
    {
        var results = new ResultRecorder();
        var errors = new StringWriter();

        int exitCode = SessionHostProgram.Run(
            ["--request", @"C:\ws\req.json"],
            new ThrowingLauncher("node.exe is missing"),
            errors,
            _ => RequestJson(),
            results.Write);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.False(results.Result!.Launched);
        Assert.Null(results.Result.ExitCode);
        Assert.Equal("node.exe is missing", results.Result.Error);
        Assert.Contains("node.exe is missing", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableRequestIsRecordedWithoutLaunchingAnything()
    {
        var launcher = new RecordingLauncher(0);
        var results = new ResultRecorder();

        int exitCode = SessionHostProgram.Run(
            ["--request", @"C:\ws\req.json"],
            launcher,
            new StringWriter(),
            _ => throw new FileNotFoundException("the request file is missing"),
            results.Write);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.Null(launcher.Request);
        Assert.False(results.Result!.Launched);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--request")]
    [InlineData("--request|")]
    [InlineData(@"--run|C:\ws\req.json")]
    [InlineData(@"--request|C:\ws\req.json|extra")]
    public void OnlyAWellFormedRequestOptionIsAccepted(string packedArgs)
    {
        string[] args = packedArgs.Length == 0
            ? []
            : packedArgs.Split('|');
        // The helper is reachable from inside the session, so it must never
        // become a general-purpose runner.
        var launcher = new RecordingLauncher(0);
        var errors = new StringWriter();

        int exitCode = SessionHostProgram.Run(
            args,
            launcher,
            errors,
            _ => RequestJson(),
            (_, _) => throw new InvalidOperationException(
                "no result file exists without a request path"));

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.Null(launcher.Request);
        Assert.Contains("usage", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnwritableResultStillReportsTheOriginalFailure()
    {
        var errors = new StringWriter();

        int exitCode = SessionHostProgram.Run(
            ["--request", @"C:\ws\req.json"],
            new ThrowingLauncher("node.exe is missing"),
            errors,
            _ => RequestJson(),
            (_, _) => throw new UnauthorizedAccessException("workspace is read-only"));

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);

        string written = errors.ToString();
        Assert.Contains("node.exe is missing", written, StringComparison.Ordinal);
        Assert.Contains("workspace is read-only", written, StringComparison.Ordinal);
    }
}
