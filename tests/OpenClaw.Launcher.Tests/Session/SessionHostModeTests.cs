using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;
using SessionHostProgram = OpenClaw.SessionHost.Program;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionHostModeTests
{
    [Fact]
    public void UndefinedModeIsRejectedBeforeLaunching()
    {
        const SessionLaunchMode mode = (SessionLaunchMode)42;
        var launcher = new RecordingLauncher();
        SessionLaunchResult? result = null;
        var request = new SessionLaunchRequest
        {
            RequestId = "request-1",
            Mode = mode,
            Executable = @"C:\fixture\openclaw.exe",
            Arguments = [],
            WorkingDirectory = @"C:\fixture",
            LogPath = @"C:\fixture\gateway.log",
            StatusPath = @"C:\fixture\gateway.json"
        };

        int exitCode = SessionHostProgram.Run(
            ["--request", @"C:\shared\request.json"],
            launcher,
            TextWriter.Null,
            _ => SessionLaunchProtocol.SerializeRequest(request),
            (_, value) => result = value);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.Equal(0, launcher.RunCount);
        Assert.Equal(0, launcher.StartCount);
        Assert.NotNull(result);
        Assert.False(result.Launched);
        Assert.Contains("mode", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingLauncher : ISessionProcessLauncher
    {
        public int RunCount { get; private set; }
        public int StartCount { get; private set; }

        public int Run(SessionLaunchRequest request)
        {
            RunCount++;
            return 0;
        }

        public SessionDetachedProcess Start(
            SessionLaunchRequest request,
            string helperExecutablePath)
        {
            StartCount++;
            return new SessionDetachedProcess(1, DateTimeOffset.UtcNow);
        }
    }
}
