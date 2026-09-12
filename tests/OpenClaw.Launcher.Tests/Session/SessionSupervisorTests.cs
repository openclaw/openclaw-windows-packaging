using System.Diagnostics;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Drives the real supervisor against a real child process, because its whole
/// job is to own output and lifetime that a fake would simply assert away.
/// </summary>
public sealed class SessionSupervisorTests : IDisposable
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

    private string LogPath => Path.Combine(_root, "gateway.log");

    private string StatusPath => Path.Combine(_root, "gateway.status.json");

    private string CommandProcessor => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");

    private SessionLaunchRequest Request(params string[] arguments) =>
        new()
        {
            RequestId = "r1",
            Mode = SessionLaunchMode.Detached,
            Executable = CommandProcessor,
            Arguments = arguments,
            WorkingDirectory = _root,
            LogPath = LogPath,
            StatusPath = StatusPath
        };

    private int RunSupervisor(SessionLaunchRequest request)
    {
        string requestPath = SessionSupervisor.RequestPathFor(StatusPath);
        File.WriteAllText(requestPath, SessionLaunchProtocol.SerializeRequest(request));
        return SessionSupervisor.Run(requestPath, File.ReadAllText, File.WriteAllText);
    }

    [Fact]
    public void TheApplicationsOutputIsCapturedInTheLog()
    {
        // A detached process has no console to inherit, so without this its
        // output would be written into a handle that closes as soon as the
        // launching execution returns.
        RunSupervisor(Request("/c", "echo", "hello-from-the-gateway"));

        Assert.Contains(
            "hello-from-the-gateway",
            File.ReadAllText(LogPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StandardErrorIsCapturedToo()
    {
        RunSupervisor(Request("/c", "echo trouble 1>&2"));

        Assert.Contains("trouble", File.ReadAllText(LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TheApplicationsExitCodeIsReturned()
    {
        Assert.Equal(7, RunSupervisor(Request("/c", "exit 7")));
    }

    [Fact]
    public void TheStatusFileRecordsThatTheLaunchEnded()
    {
        RunSupervisor(Request("/c", "exit 3"));

        SessionSupervisorStatus status =
            SessionInspectProtocol.ReadStatus(File.ReadAllText(StatusPath));

        Assert.Equal(SessionSupervisorStatus.ExitedState, status.State);
        Assert.Contains("3", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRequestFileIsRemovedAfterwards()
    {
        RunSupervisor(Request("/c", "exit 0"));

        Assert.False(File.Exists(SessionSupervisor.RequestPathFor(StatusPath)));
    }

    [Fact]
    public void AnApplicationThatCannotStartIsRecordedRatherThanLostSilently()
    {
        SessionLaunchRequest request = Request("/c", "exit 0") with
        {
            Executable = Path.Combine(_root, "does-not-exist.exe")
        };

        int exitCode = RunSupervisor(request);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        SessionSupervisorStatus status =
            SessionInspectProtocol.ReadStatus(File.ReadAllText(StatusPath));
        Assert.Equal(SessionSupervisorStatus.ExitedState, status.State);
        Assert.NotNull(status.Detail);
    }

    [Fact]
    public void TheEnvironmentContractReachesTheApplication()
    {
        SessionLaunchRequest request = Request("/c", "echo %OPENCLAW_SUPERVISOR_MODE%") with
        {
            Environment = new Dictionary<string, string>
            {
                ["OPENCLAW_SUPERVISOR_MODE"] = "external"
            }
        };

        RunSupervisor(request);

        Assert.Contains("external", File.ReadAllText(LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TheApplicationRunsInTheRequestedWorkingDirectory()
    {
        // At logon there is no meaningful inherited directory, so it must be
        // stated rather than assumed.
        RunSupervisor(Request("/c", "cd"));

        Assert.Contains(_root, File.ReadAllText(LogPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheApplicationDoesNotOutliveItsSupervisor()
    {
        // Measured defect: without a kill-on-close job the application kept
        // running after its supervisor was killed, and kept its listening port
        // with nothing owning it. A later start would then find the port taken
        // by something it could not prove was its own and could not safely
        // stop.
        //
        // Closing the job handle is what a supervisor's exit does, however it
        // exits, so it is the faithful way to exercise the guarantee.
        using Process application = GuestProcessObserverTests.StartLongRunningProcess();
        try
        {
            using (GuestKillOnCloseJob job = GuestKillOnCloseJob.Create())
            {
                job.Assign(application);
                Assert.NotNull(GuestProcessObserver.GetStartTimeUtc(application.Id));
            }

            Assert.True(
                WaitForExit(application.Id),
                "The application was still running after its supervisor's job closed.");
        }
        finally
        {
            GuestProcessObserverTests.Kill(application);
        }
    }

    [Fact]
    public void AnApplicationThatIsStillRunningKeepsItsPortUntilTheSupervisorGoes()
    {
        // The other half of the same guarantee: the job must not take the
        // application down early, or the gateway would die the moment it was
        // adopted.
        using Process application = GuestProcessObserverTests.StartLongRunningProcess();
        try
        {
            using GuestKillOnCloseJob job = GuestKillOnCloseJob.Create();
            job.Assign(application);

            Thread.Sleep(200);

            Assert.NotNull(GuestProcessObserver.GetStartTimeUtc(application.Id));
        }
        finally
        {
            GuestProcessObserverTests.Kill(application);
        }
    }

    private static bool WaitForExit(int processId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (GuestProcessObserver.GetStartTimeUtc(processId) is null)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    [Fact]
    public void AnUnreadableRequestFailsWithoutStartingAnything()
    {
        string requestPath = SessionSupervisor.RequestPathFor(StatusPath);
        File.WriteAllText(requestPath, "{ not json");

        int exitCode = SessionSupervisor.Run(requestPath, File.ReadAllText, File.WriteAllText);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void AppendingPreservesAnEarlierLaunchesOutput()
    {
        RunSupervisor(Request("/c", "echo first"));
        RunSupervisor(Request("/c", "echo second"));

        string log = File.ReadAllText(LogPath);
        Assert.Contains("first", log, StringComparison.Ordinal);
        Assert.Contains("second", log, StringComparison.Ordinal);
    }
}

public sealed class SessionLaunchProtocolDetachedTests
{
    private static SessionLaunchRequest Valid() =>
        new()
        {
            RequestId = "r1",
            Mode = SessionLaunchMode.Detached,
            Executable = @"C:\Windows\System32\cmd.exe",
            Arguments = [],
            WorkingDirectory = @"C:\",
            LogPath = @"C:\state\gateway.log",
            StatusPath = @"C:\state\gateway.status.json"
        };

    [Fact]
    public void ADetachedRequestRoundTrips()
    {
        SessionLaunchRequest parsed = SessionLaunchProtocol.ReadRequest(
            SessionLaunchProtocol.SerializeRequest(Valid()));

        Assert.Equal(SessionLaunchMode.Detached, parsed.Mode);
        Assert.Equal(@"C:\state\gateway.log", parsed.LogPath);
        Assert.Equal(@"C:\state\gateway.status.json", parsed.StatusPath);
    }

    [Fact]
    public void TheModeIsWrittenByNameSoTheControlFileCanBeRead()
    {
        // These files are read by people diagnosing a gateway that did not
        // start, and a bare number tells them nothing.
        Assert.Contains(
            "\"mode\":\"Detached\"",
            SessionLaunchProtocol.SerializeRequest(Valid()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestWithoutAModeStaysAttached()
    {
        // Existing OpenClaw invocations must keep waiting for their process.
        SessionLaunchRequest parsed = SessionLaunchProtocol.ReadRequest(
            """
            {
              "schemaVersion": 2,
              "requestId": "r1",
              "executable": "C:\\Windows\\System32\\cmd.exe",
              "arguments": [],
              "workingDirectory": "C:\\"
            }
            """);

        Assert.Equal(SessionLaunchMode.Attached, parsed.Mode);
    }

    [Theory]
    [InlineData("log")]
    [InlineData("status")]
    public void ADetachedRequestMissingItsEvidencePathsIsRejected(string missing)
    {
        SessionLaunchRequest request = missing == "log"
            ? Valid() with { LogPath = null }
            : Valid() with { StatusPath = null };

        Assert.Throws<SessionLaunchException>(
            () => SessionLaunchProtocol.ReadRequest(
                SessionLaunchProtocol.SerializeRequest(request)));
    }

    [Fact]
    public void AnAttachedRequestNeedsNoEvidencePaths()
    {
        SessionLaunchRequest request = Valid() with
        {
            Mode = SessionLaunchMode.Attached,
            LogPath = null,
            StatusPath = null
        };

        SessionLaunchProtocol.ReadRequest(SessionLaunchProtocol.SerializeRequest(request));
    }

    [Fact]
    public void AStaleSchemaVersionIsRejectedRatherThanInterpreted()
    {
        // The helper and the launcher ship together, so a mismatch means a
        // stale file or a mixed installation.
        Assert.Throws<SessionLaunchException>(
            () => SessionLaunchProtocol.ReadRequest(
                SessionLaunchProtocol.SerializeRequest(Valid() with { SchemaVersion = 1 })));
    }

    [Fact]
    public void ADetachedResultCarriesTheSupervisorIdentity()
    {
        var result = new SessionLaunchResult
        {
            RequestId = "r1",
            Launched = true,
            ProcessId = 1234,
            ProcessStartTimeUtc = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero)
        };

        SessionLaunchResult parsed = SessionLaunchProtocol.ReadResult(
            SessionLaunchProtocol.SerializeResult(result));

        Assert.Equal(1234, parsed.ProcessId);
        Assert.Equal(result.ProcessStartTimeUtc, parsed.ProcessStartTimeUtc);
    }
}
