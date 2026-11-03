using System.CommandLine;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;

namespace OpenClaw.Launcher.Tests;

public sealed class PayloadPrewarmTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string Application => Path.Combine(_root, "app");

    private string ListPath => PayloadPrewarm.GetListPath(_root);

    private void WriteApplicationFile(string relativePath, int length)
    {
        string path = Path.Combine(Application, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    private void WriteList(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ListPath)!);
        File.WriteAllText(ListPath, json);
    }

    private static string ListJson(params string[] files) =>
        "{\"schemaVersion\":1,\"files\":[" +
        string.Join(",", files.Select(static file => $"\"{file.Replace("\\", "\\\\", StringComparison.Ordinal)}\"")) +
        "]}";

    private PayloadPrewarmResult Run(
        PayloadPrewarmLimits? limits = null,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default) =>
        PayloadPrewarm.Run(
            ListPath,
            Application,
            limits ?? PayloadPrewarmLimits.Default,
            clock ?? TimeProvider.System,
            cancellationToken);

    [Fact]
    public void ListedFilesAreReadAndUnsafeOrMissingEntriesAreSkipped()
    {
        WriteApplicationFile("openclaw.mjs", 10);
        WriteApplicationFile("dist/chunk.js", 200_000);
        File.WriteAllText(Path.Combine(_root, "outside.js"), "outside");
        WriteList(ListJson(
            "openclaw.mjs",
            "dist/chunk.js",
            "dist/missing.js",
            "../outside.js",
            "dist\\chunk.js",
            "C:/Windows/win.ini",
            "/openclaw.mjs",
            "dist//chunk.js",
            "./openclaw.mjs"));

        PayloadPrewarmResult result = Run();

        Assert.Equal(PayloadPrewarmOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.FilesRead);
        Assert.Equal(7, result.FilesSkipped);
        Assert.Equal(200_010, result.BytesRead);
    }

    [Fact]
    public void AMissingListIsANoOp()
    {
        Assert.Equal(PayloadPrewarmOutcome.MissingList, Run().Outcome);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"schemaVersion\":2,\"files\":[\"openclaw.mjs\"]}")]
    [InlineData("{\"schemaVersion\":1}")]
    public void AnUnusableListReadsNothing(string json)
    {
        WriteApplicationFile("openclaw.mjs", 10);
        WriteList(json);

        PayloadPrewarmResult result = Run();

        Assert.Equal(PayloadPrewarmOutcome.InvalidList, result.Outcome);
        Assert.Equal(0, result.FilesRead);
    }

    [Fact]
    public void TheFileBudgetStopsThePass()
    {
        WriteApplicationFile("a.js", 1);
        WriteApplicationFile("b.js", 1);
        WriteApplicationFile("c.js", 1);
        WriteList(ListJson("a.js", "b.js", "c.js"));

        PayloadPrewarmResult result = Run(new PayloadPrewarmLimits(2, long.MaxValue, TimeSpan.MaxValue));

        Assert.Equal(PayloadPrewarmOutcome.FileLimitReached, result.Outcome);
        Assert.Equal(2, result.FilesRead);
    }

    [Fact]
    public void TheByteBudgetStopsThePass()
    {
        WriteApplicationFile("a.js", 100_000);
        WriteApplicationFile("b.js", 100_000);
        WriteList(ListJson("a.js", "b.js"));

        PayloadPrewarmResult result = Run(new PayloadPrewarmLimits(int.MaxValue, 50_000, TimeSpan.MaxValue));

        Assert.Equal(PayloadPrewarmOutcome.ByteLimitReached, result.Outcome);
        Assert.Equal(1, result.FilesRead);
        Assert.Equal(50_000, result.BytesRead);
    }

    [Fact]
    public void TheDeadlineStopsThePass()
    {
        WriteApplicationFile("a.js", 1);
        WriteList(ListJson("a.js"));

        PayloadPrewarmResult result = Run(new PayloadPrewarmLimits(int.MaxValue, long.MaxValue, TimeSpan.Zero));

        Assert.Equal(PayloadPrewarmOutcome.DeadlineReached, result.Outcome);
        Assert.Equal(0, result.FilesRead);
    }

    [Fact]
    public void TheDeadlineStopsAFileBetweenChunks()
    {
        WriteApplicationFile("large.js", 200_000);
        WriteList(ListJson("large.js"));

        // Each timestamp read advances one second: the check before the file
        // sees 1 s, and the check after the first chunk sees 2 s.
        PayloadPrewarmResult result = Run(
            new PayloadPrewarmLimits(int.MaxValue, long.MaxValue, TimeSpan.FromSeconds(2)),
            new SteppingClock());

        Assert.Equal(PayloadPrewarmOutcome.DeadlineReached, result.Outcome);
        Assert.Equal(0, result.FilesRead);
        Assert.Equal(64 * 1024, result.BytesRead);
    }

    [Fact]
    public void TheLifetimeBoundLogsThenEndsTheWorkerAtItsLimit()
    {
        CapturingTimerClock clock = new();
        List<string> log = [];
        int terminated = 0;

        using ITimer bound = PayloadPrewarm.ArmLifetimeBound(
            clock,
            TimeSpan.FromSeconds(315),
            TimeSpan.FromSeconds(5),
            log.Add,
            () => terminated++);

        Assert.Equal(TimeSpan.FromSeconds(315), clock.DueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, clock.Period);
        Assert.Equal(0, terminated);

        clock.Fire();

        Assert.Equal(1, terminated);
        Assert.Equal(["Payload prewarm reached its 315 s lifetime bound; ending the worker."], log);
    }

    [Fact]
    public void ABlockedLogDoesNotHoldTheWorkerPastItsBound()
    {
        CapturingTimerClock clock = new();
        using ManualResetEventSlim release = new();
        int terminated = 0;

        using ITimer bound = PayloadPrewarm.ArmLifetimeBound(
            clock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(50),
            _ => release.Wait(),
            () => terminated++);
        try
        {
            clock.Fire();

            Assert.Equal(1, terminated);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void AFailingLogStillEndsTheWorker()
    {
        CapturingTimerClock clock = new();
        int terminated = 0;

        using ITimer bound = PayloadPrewarm.ArmLifetimeBound(
            clock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            _ => throw new IOException("log unavailable"),
            () => terminated++);

        clock.Fire();

        Assert.Equal(1, terminated);
    }

    [Fact]
    public void CancellationStopsThePass()
    {
        WriteApplicationFile("a.js", 1);
        WriteList(ListJson("a.js"));

        PayloadPrewarmResult result = Run(cancellationToken: new CancellationToken(canceled: true));

        Assert.Equal(PayloadPrewarmOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, result.FilesRead);
    }

    [Fact]
    public void TeardownAfterWorkerRegistrationCancelsBeforeAnyPayloadRead()
    {
        WriteApplicationFile("a.js", 1);
        WriteList(ListJson("a.js"));
        string scope = Guid.NewGuid().ToString("N");
        var lifecycleLock = new AlwaysFreeLock();
        List<string> log = [];

        PayloadPrewarmResult result = PayloadPrewarm.RunRegisteredPass(
            lifecycleLock,
            () => Assert.Equal(1, lifecycleLock.HeldCount),
            scope,
            token =>
            {
                Assert.Equal(0, lifecycleLock.HeldCount);
                using (lifecycleLock.TryAcquire(TimeSpan.Zero))
                {
                    PayloadPrewarm.SignalCancel(scope, log.Add);
                }

                Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
                return Run(cancellationToken: token);
            },
            CancellationToken.None);

        Assert.Equal(PayloadPrewarmOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, result.BytesRead);
        Assert.Equal(["Asked the running payload prewarm to stop."], log);
        log.Clear();
        PayloadPrewarm.SignalCancel(scope, log.Add);
        Assert.Empty(log);
    }

    [Fact]
    public void AWorkerDelayedUntilAfterTeardownNeverRegistersOrReads()
    {
        var lifecycleLock = new AlwaysFreeLock();
        string scope = Guid.NewGuid().ToString("N");
        bool read = false;

        SessionException failure = Assert.Throws<SessionException>(() =>
            PayloadPrewarm.RunRegisteredPass(
                lifecycleLock,
                () =>
                {
                    Assert.Equal(1, lifecycleLock.HeldCount);
                    throw new SessionException("OpenClaw has not been set up.");
                },
                scope,
                _ =>
                {
                    read = true;
                    return Run();
                },
                CancellationToken.None));

        Assert.Contains("not been set up", failure.Message, StringComparison.Ordinal);
        Assert.False(read);
        Assert.Equal(0, lifecycleLock.HeldCount);
        List<string> log = [];
        PayloadPrewarm.SignalCancel(scope, log.Add);
        Assert.Empty(log);
    }

    [Fact]
    public void AFailedPassReleasesItsCancellationRegistration()
    {
        string scope = Guid.NewGuid().ToString("N");
        _ = Assert.Throws<IOException>(() => PayloadPrewarm.RunRegisteredPass(
            new AlwaysFreeLock(),
            () => { },
            scope,
            _ => throw new IOException("fixture read failure"),
            CancellationToken.None));

        List<string> log = [];
        PayloadPrewarm.SignalCancel(scope, log.Add);
        Assert.Empty(log);
    }

    [Fact]
    public void TheWorkerEntersBackgroundModeBeforeReadingAnyFile()
    {
        WriteApplicationFile("a.js", 1);
        List<string> log = [];

        // The list appears only once background mode is entered, so a read that
        // ran first would find no list.
        PayloadPrewarmResult result = PayloadPrewarm.RunPass(
            () =>
            {
                WriteList(ListJson("a.js"));
                return null;
            },
            ListPath,
            Application,
            PayloadPrewarmLimits.Default,
            TimeProvider.System,
            log.Add,
            CancellationToken.None);

        Assert.Equal(PayloadPrewarmOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.FilesRead);
        Assert.Equal([result.Describe()], log);
    }

    [Fact]
    public void ABackgroundModeFailureSkipsThePass()
    {
        WriteApplicationFile("a.js", 1);
        WriteList(ListJson("a.js"));
        List<string> log = [];

        PayloadPrewarmResult result = PayloadPrewarm.RunPass(
            () => 5,
            ListPath,
            Application,
            PayloadPrewarmLimits.Default,
            TimeProvider.System,
            log.Add,
            CancellationToken.None);

        Assert.Equal(PayloadPrewarmOutcome.BackgroundModeUnavailable, result.Outcome);
        Assert.Equal(0, result.FilesRead);
        Assert.Equal(0, result.BytesRead);
        Assert.Equal(
            ["Payload prewarm skipped: background mode could not be entered (Win32 error 5); no files were read."],
            log);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData(" OFF ")]
    [InlineData("no")]
    public void TheOperatorCanOptOut(string value)
    {
        List<string> started = [];
        List<string> log = [];

        PayloadPrewarm.TryStart(
            name => name == OpenClawRuntimeEnvironment.PayloadPrewarmVariable ? value : null,
            () => throw new InvalidOperationException("Opting out must not inspect package identity."),
            _root,
            _root,
            path =>
            {
                started.Add(path);
                return 1;
            },
            log.Add);

        Assert.Empty(started);
        Assert.Contains(log, line => line.Contains(OpenClawRuntimeEnvironment.PayloadPrewarmVariable, StringComparison.Ordinal));
        Assert.False(AutoBehaviorPolicy.IsPayloadPrewarmEnabled(_ => value));
    }

    [Fact]
    public void AnUnpackagedHostDoesNotStartTheWorker()
    {
        List<string> started = [];

        PayloadPrewarm.TryStart(_ => null, () => null, _root, _root, path =>
        {
            started.Add(path);
            return 1;
        }, _ => { });

        Assert.Empty(started);
    }

    [Fact]
    public void ThePackagedControlAliasStartsTheWorker()
    {
        WriteList(ListJson("openclaw.mjs"));
        File.WriteAllText(
            Path.Combine(_root, PackageExecutionAlias.ManifestFileName),
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
              <Applications>
                <Application Id="Control" Executable="openclaw.exe">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias><uap5:ExecutionAlias Alias="clawctl-dev.exe" /></uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
        List<string> started = [];
        List<string> log = [];

        PayloadPrewarm.TryStart(_ => null, () => "Family_abc", _root, @"C:\LocalAppData", path =>
        {
            started.Add(path);
            return 42;
        }, log.Add);

        Assert.Equal(
            [@"C:\LocalAppData\Microsoft\WindowsApps\Family_abc\clawctl-dev.exe"],
            started);
        Assert.Contains(log, line => line.Contains("process 42", StringComparison.Ordinal));
    }

    [Fact]
    public void AStartFailureIsLoggedAndNeverThrown()
    {
        WriteList(ListJson("openclaw.mjs"));
        List<string> log = [];

        PayloadPrewarm.TryStart(
            _ => null,
            () => throw new InvalidOperationException("identity failure"),
            _root,
            _root,
            _ => 1,
            log.Add);

        Assert.Contains(log, line => line.Contains("identity failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheHiddenCommandRunsItsHandlerAndStaysOutOfHelp()
    {
        int calls = 0;
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(1),
            Status = _ => Task.FromResult(1),
            CollectLogs = (_, _) => Task.FromResult(1),
            Teardown = (_, _) => Task.FromResult(1),
            PowerShell = (_, _) => Task.FromResult(1),
            GatewayStart = (_, _) => Task.FromResult(1),
            GatewayStatus = _ => Task.FromResult(1),
            GatewayStop = _ => Task.FromResult(1),
            GatewayRestart = _ => Task.FromResult(1),
            PayloadPrewarm = _ =>
            {
                calls++;
                return Task.FromResult(0);
            }
        });

        int exitCode = await root.Parse(PayloadPrewarm.CommandName).InvokeAsync().ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(
            ClawCtlHelp.Describe(root).Commands,
            command => command.Term == PayloadPrewarm.CommandName);
    }

    private sealed class SteppingClock : TimeProvider
    {
        private long _seconds;

        public override long TimestampFrequency => 1;

        public override long GetTimestamp() => _seconds++;
    }

    private sealed class CapturingTimerClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public TimeSpan DueTime { get; private set; }

        public TimeSpan Period { get; private set; }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            Period = period;
            return new NoOpTimer();
        }

        public void Fire() => _callback!(_state);
    }

    private sealed class NoOpTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
