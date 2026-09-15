using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// A gateway client whose observations are dictated by the test, so the
/// controller's decisions can be driven through every state the guest can
/// report.
/// </summary>
internal sealed class FakeSessionGatewayClient : ISessionGatewayClient
{
    public List<string> Calls { get; } = [];

    public SessionInspectResult Inspection { get; set; } = new();

    public SessionInspectResult? StopResult { get; set; }

    public GatewayStartOutcome StartOutcome { get; set; } = new(
        1234,
        new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        "status.json",
        "gateway.log");

    public Exception? StartFailure { get; set; }

    public GatewayStartRequest? StartRequest { get; private set; }

    public Task<GatewayStartOutcome> StartAsync(
        SessionRecord session,
        GatewayStartRequest request,
        CancellationToken cancellationToken)
    {
        Calls.Add("start");
        StartRequest = request;
        return StartFailure is null
            ? Task.FromResult(StartOutcome)
            : Task.FromException<GatewayStartOutcome>(StartFailure);
    }

    public Task<SessionInspectResult> InspectAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        CancellationToken cancellationToken)
    {
        Calls.Add($"inspect:{gateway.ProcessId}");
        return Task.FromResult(Inspection);
    }

    public Task<SessionInspectResult> StopAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        CancellationToken cancellationToken)
    {
        Calls.Add($"stop:{gateway.ProcessId}");
        return Task.FromResult(StopResult ?? new SessionInspectResult());
    }
}

public sealed class GatewayControllerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public async Task SessionGatewayLaunchPrefixesPathWithTheAgentNodeDirectory()
    {
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        string packagedHelper = Path.Combine(_root, "openclaw-session-host.exe");
        await File.WriteAllTextAsync(packagedHelper, "helper");
        string stagedHelper = SessionHelperStager.Stage(packagedHelper, workspace);
        SessionLaunchRequest? delivered = null;
        var backend = new FakeMxcSessionClient();
        backend.ExecuteBehavior = request =>
        {
            string requestPath = Directory.GetFiles(workspace, "gateway-*.json")
                .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            delivered = SessionLaunchProtocol.ReadRequest(File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = delivered.RequestId,
                    Launched = true,
                    ProcessId = 1234,
                    ProcessStartTimeUtc = DateTimeOffset.UtcNow
                }));
            return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
        };
        var client = new SessionGatewayClient(backend, _ => { });
        string nodePath = Path.Combine(
            workspace, "agent", "NodeJS", "node-v24", "node.exe");

        await client.StartAsync(
            new SessionRecord
            {
                SchemaVersion = SessionStateStore.CurrentSchemaVersion,
                SandboxId = "iso:test",
                ApplicationId = "PFN:test",
                WorkspacePath = workspace,
                CreatedUtc = DateTimeOffset.UtcNow
            },
            new GatewayStartRequest(
                stagedHelper,
                nodePath,
                Path.Combine(_root, "app"),
                workspace,
                null),
            CancellationToken.None);

        Assert.NotNull(delivered);
        Assert.Equal(Path.GetDirectoryName(nodePath), delivered.PathPrefix);
    }
    private readonly FakeSessionGatewayClient _client = new();
    private readonly FakeMxcSessionClient _backend = new();
    private const string ApplicationId = "PFN:OpenClaw.Gateway_test";

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

    private GatewayStateStore Store => new(Path.Combine(_root, "gateway.json"));

    private SessionCoordinator CreateSessions() =>
        new(
            _backend,
            new SessionStateStore(Path.Combine(_root, "session.json")),
            new AlwaysFreeLock(),
            ApplicationId,
            _ => { });

    private GatewayController CreateController(
        GatewayStateStore? store = null)
    {
        var sessionStore = new SessionStateStore(Path.Combine(_root, "session.json"));
        if (sessionStore.Read(ApplicationId).Fault == SessionStateFault.Missing)
        {
            sessionStore.Write(new SessionRecord
            {
                ApplicationId = ApplicationId,
                SandboxId = "iso:sandbox1",
                WorkspacePath = _root,
                CreatedUtc = DateTimeOffset.UnixEpoch
            });
        }
        SessionCoordinator sessions = CreateSessions();
        return new GatewayController(
            sessions,
            _client,
            store ?? Store,
            _ => Task.FromResult(new GatewayStartRequest(
                HelperPath: Path.Combine(_root, "openclaw-session-host.exe"),
                NodePath: @"C:\Program Files\nodejs\node.exe",
                ApplicationDirectory: Path.Combine(_root, "app"),
                Port: null)),
            _ => { },
            () => sessions.GetRecordedStatus().Record ?? throw new SessionException("Run setup."),
            new AlwaysFreeLock());
    }

    private static SessionInspectResult Healthy() => new()
    {
        ProcessFound = true,
        StartTimeMatches = true,
        PortListening = true,
        ListenerOwned = true,
        ListeningPorts = [18789]
    };

    // The user has to be told where their gateway actually is. OpenClaw picks
    // the port from its own configuration, so only the observed value is true.
    [Fact]
    public async Task StartReportsTheObservedPortRatherThanAnAssumedOne()
    {
        _client.Inspection = Healthy();

        GatewayStartResult result = await CreateController()
            .StartAsync("helper.exe", CancellationToken.None);

        Assert.Null(result.Record.Port);
        Assert.Equal([18789], result.Record.ObservedPorts);
        Assert.Contains("port 18789", result.Message, StringComparison.Ordinal);
    }

    // A pinned port the gateway is not on means OpenClaw's configuration moved
    // it. Reporting that as healthy would send the user to the wrong place.
    [Fact]
    public async Task APinnedPortTheGatewayIsNotOnIsReportedAsUnhealthy()
    {
        RecordGateway();
        Store.Write(Store.Read().Record! with { Port = 9100 });
        _client.Inspection = Healthy() with { ListenerOwned = false };

        GatewayStatusReport report = await CreateController()
            .GetStatusAsync("helper.exe", CancellationToken.None);

        Assert.Equal(GatewayState.Unhealthy, report.State);
        Assert.Contains("18789", report.Detail!, StringComparison.Ordinal);
        Assert.Contains("gateway.port", report.Detail!, StringComparison.Ordinal);
    }

    private void RecordGateway(int processId = 1234, bool autostartDisabled = false) =>
        Store.Write(new GatewayRecord
        {
            SandboxId = "iso:sandbox1",
            ProcessId = processId,
            ProcessStartTimeUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            Port = null,
            StatusPath = "status.json",
            AutostartDisabled = autostartDisabled
        });

    private void RecordPendingGateway(int processId = 0) =>
        Store.Write(new GatewayRecord
        {
            SandboxId = "iso:sandbox1",
            ProcessId = processId,
            LaunchPending = true,
            ProcessStartTimeUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            Port = null,
            StatusPath = "status.json"
        });

    [Fact]
    public void SupersededSessionReconciliationRemovesOnlyTheMatchingGatewayRecord()
    {
        RecordGateway();

        bool removed = Store.ClearForSupersededSession("iso:sandbox1");

        Assert.True(removed);
        Assert.Equal(GatewayStateFault.Missing, Store.Read().Fault);
    }

    [Fact]
    public void SupersededSessionReconciliationPreservesAnUnknownGatewayRecord()
    {
        RecordGateway();

        bool removed = Store.ClearForSupersededSession("iso:stale-sandbox");

        Assert.False(removed);
        Assert.Equal("iso:sandbox1", Store.Read().Record!.SandboxId);
    }

    [Fact]
    public async Task StatusReportsNotStartedWithoutProvisioningASession()
    {
        GatewayStatusReport report = await CreateController()
            .GetStatusAsync("helper.exe", CancellationToken.None);

        Assert.Equal(GatewayState.NotStarted, report.State);
        Assert.Empty(_backend.Calls);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task StatusReportsARunningGateway()
    {
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);

        GatewayStatusReport report =
            await controller.GetStatusAsync("helper.exe", CancellationToken.None);

        Assert.Equal(GatewayState.Running, report.State);
    }

    [Fact]
    public async Task ARecordedGatewayThatIsGoneIsReportedAsStopped()
    {
        // Stopping the session terminates detached work silently, so a record
        // routinely outlives the process it describes.
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);
        _client.Inspection = new SessionInspectResult();

        GatewayStatusReport report =
            await controller.GetStatusAsync("helper.exe", CancellationToken.None);

        Assert.Equal(GatewayState.Stopped, report.State);
    }

    [Fact]
    public async Task ALiveProcessThatIsNotServingIsReportedAsUnhealthy()
    {
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);
        _client.Inspection = Healthy() with { PortListening = false, ListenerOwned = false };

        GatewayStatusReport report =
            await controller.GetStatusAsync("helper.exe", CancellationToken.None);

        Assert.Equal(GatewayState.Unhealthy, report.State);
        Assert.NotNull(report.Detail);
    }

    [Fact]
    public async Task AnUnansweredInspectionIsReportedAsUnknown()
    {
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);
        _client.Inspection = new SessionInspectResult { Error = "no answer" };

        GatewayStatusReport report =
            await controller.GetStatusAsync("helper.exe", CancellationToken.None);

        Assert.Equal(GatewayState.Unknown, report.State);
    }

    [Fact]
    public async Task AnUnreadableRecordIsUnknownRatherThanNotStarted()
    {
        string path = Path.Combine(_root, "gateway.json");
        await File.WriteAllTextAsync(path, "{ not json", CancellationToken.None);

        GatewayStatusReport report = await CreateController(new GatewayStateStore(path))
            .GetStatusAsync("helper.exe", CancellationToken.None);

        Assert.Equal(GatewayState.Unknown, report.State);
    }

    [Fact]
    public async Task StartingRecordsTheSupervisorIdentityAndItsPort()
    {
        _client.Inspection = Healthy();

        GatewayStartResult result = await CreateController()
            .StartAsync("helper.exe", CancellationToken.None);

        Assert.Equal(GatewayState.Running, result.State);
        Assert.False(result.AlreadyRunning);
        Assert.Equal(_client.StartOutcome.ProcessId, result.Record.ProcessId);
        Assert.Equal(
            _client.StartOutcome.ProcessStartTimeUtc,
            result.Record.ProcessStartTimeUtc);
        Assert.Null(result.Record.Port);
    }

    [Fact]
    public async Task StartingUsesThePreparedSessionWithoutProvisioning()
    {
        _client.Inspection = Healthy();

        await CreateController().StartAsync("helper.exe", CancellationToken.None);

        Assert.DoesNotContain("provision", _backend.Calls);
        Assert.Contains("start:iso:sandbox1", _backend.Calls);
    }

    [Fact]
    public async Task ASecondStartDoesNotStartASecondGateway()
    {
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);

        GatewayStartResult result =
            await controller.StartAsync("helper.exe", CancellationToken.None);

        Assert.True(result.AlreadyRunning);
        Assert.Single(_client.Calls, call => call == "start");
    }

    [Fact]
    public async Task AConfirmedPendingGatewayIsRetainedWithoutStartingAnother()
    {
        RecordPendingGateway(processId: 1234);
        _client.Inspection = Healthy();

        GatewayStartResult result = await CreateController()
            .StartAsync("helper.exe", CancellationToken.None);

        Assert.True(result.AlreadyRunning);
        Assert.DoesNotContain("start", _client.Calls);
        Assert.False(Store.Read().Record!.LaunchPending);
    }

    [Fact]
    public async Task AnExitedPendingGatewayIsReconciledBeforeRetrying()
    {
        RecordPendingGateway();
        _client.Inspection = new SessionInspectResult();

        GatewayStartResult result = await CreateController()
            .StartAsync("helper.exe", CancellationToken.None);

        Assert.False(result.AlreadyRunning);
        Assert.Single(_client.Calls, call => call == "start");
        Assert.False(result.Record.LaunchPending);
    }

    [Fact]
    public async Task AnAmbiguousPendingGatewayIsNotReplacedOrStopped()
    {
        RecordPendingGateway(processId: 1234);
        _client.Inspection = new SessionInspectResult
        {
            ProcessFound = true,
            StartTimeMatches = true
        };

        await Assert.ThrowsAsync<SessionException>(
            () => CreateController().StartAsync("helper.exe", CancellationToken.None));

        Assert.DoesNotContain(
            _client.Calls,
            call => call == "start" || call.StartsWith("stop:", StringComparison.Ordinal));
        Assert.True(Store.Read().Record!.LaunchPending);
    }

    [Fact]
    public async Task AGatewayThatIsNoLongerRunningIsReplaced()
    {
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);
        _client.Inspection = new SessionInspectResult();

        GatewayStartResult result =
            await controller.StartAsync("helper.exe", CancellationToken.None);

        Assert.False(result.AlreadyRunning);
        Assert.Equal(2, _client.Calls.Count(call => call == "start"));
    }

    [Fact]
    public async Task ASecondGatewayIsNotStartedBesideOneThatCannotBeObserved()
    {
        // Two processes contending for one port is worse than refusing to act.
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);
        _client.Inspection = new SessionInspectResult { Error = "no answer" };

        await Assert.ThrowsAsync<SessionException>(
            () => controller.StartAsync("helper.exe", CancellationToken.None));
        Assert.Single(_client.Calls, call => call == "start");
    }

    [Fact]
    public async Task AnExplicitAutostartChoiceSurvivesARestart()
    {
        // A later manual start must not quietly re-enable what the user turned
        // off.
        RecordGateway(autostartDisabled: true);
        _client.Inspection = new SessionInspectResult();

        GatewayStartResult result = await CreateController()
            .StartAsync("helper.exe", CancellationToken.None);

        Assert.True(result.Record.AutostartDisabled);
    }

    [Fact]
    public async Task StoppingEndsTheGatewayAndClearsItsRecord()
    {
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);

        GatewayStopResult result =
            await controller.StopAsync("helper.exe", CancellationToken.None);

        Assert.True(result.Stopped);
        Assert.Contains(
            _client.Calls,
            call => call.StartsWith("stop:", StringComparison.Ordinal));
        Assert.Equal(GatewayStateFault.Missing, Store.Read().Fault);
    }

    [Fact]
    public async Task StoppingNothingIsNotAFailure()
    {
        GatewayStopResult result = await CreateController()
            .StopAsync("helper.exe", CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.DoesNotContain(
            _client.Calls,
            call => call.StartsWith("stop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingIsTerminatedWhenIdentityCannotBeEstablished()
    {
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);
        _client.Inspection = new SessionInspectResult { Error = "no answer" };

        GatewayStopResult result =
            await controller.StopAsync("helper.exe", CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.DoesNotContain(
            _client.Calls,
            call => call.StartsWith("stop:", StringComparison.Ordinal));

        // The record survives, because the gateway it names may still be running.
        Assert.NotNull(Store.Read().Record);
    }

    [Fact]
    public async Task AReusedIdentifierIsNotTerminated()
    {
        // The recorded identifier now belongs to an unrelated process.
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);
        _client.Inspection = new SessionInspectResult
        {
            ProcessFound = true,
            StartTimeMatches = false
        };

        GatewayStopResult result =
            await controller.StopAsync("helper.exe", CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.DoesNotContain(
            _client.Calls,
            call => call.StartsWith("stop:", StringComparison.Ordinal));
        Assert.Equal(GatewayStateFault.Missing, Store.Read().Fault);
    }

    [Fact]
    public async Task StoppingDoesNotRemoveTheSession()
    {
        // Stop preserves the user's provisioned session and guest profile.
        _client.Inspection = Healthy();
        GatewayController controller = CreateController();
        await controller.StartAsync("helper.exe", CancellationToken.None);

        await controller.StopAsync("helper.exe", CancellationToken.None);

        Assert.DoesNotContain(
            _backend.Calls,
            call => call.StartsWith("deprovision:", StringComparison.Ordinal));
    }
}

public sealed class GatewayStateStoreTests : IDisposable
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

    private GatewayStateStore Store => new(Path.Combine(_root, "gateway.json"));

    private static GatewayRecord Record() => new()
    {
        SandboxId = "sandbox-1",
        ProcessId = 42,
        ProcessStartTimeUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        Port = 9100,
        StatusPath = "status.json"
    };

    [Fact]
    public void ARecordRoundTripsWithItsProcessIdentity()
    {
        Store.Write(Record());

        GatewayRecord? read = Store.Read().Record;

        Assert.NotNull(read);
        Assert.Equal(42, read.ProcessId);
        Assert.Equal(Record().ProcessStartTimeUtc, read.ProcessStartTimeUtc);
    }

    [Fact]
    public void AMissingRecordIsDistinctFromAnUnreadableOne()
    {
        Assert.Equal(GatewayStateFault.Missing, Store.Read().Fault);

        File.WriteAllText(Path.Combine(_root, "gateway.json"), "{ not json");

        Assert.Equal(GatewayStateFault.Unreadable, Store.Read().Fault);
    }

    [Fact]
    public void ANewerSchemaIsRefusedRatherThanMisread()
    {
        File.WriteAllText(
            Path.Combine(_root, "gateway.json"),
            """{"schemaVersion":99,"sandboxId":"s","processId":1}""");

        Assert.Equal(GatewayStateFault.UnsupportedSchema, Store.Read().Fault);
    }

    [Fact]
    public void ARecordWithoutAProcessIsIncomplete()
    {
        File.WriteAllText(
            Path.Combine(_root, "gateway.json"),
            """{"schemaVersion":1,"sandboxId":"s","processId":0}""");

        Assert.Equal(GatewayStateFault.Incomplete, Store.Read().Fault);
    }

    [Fact]
    public void ClearingNothingSucceeds()
    {
        Store.Clear();

        Assert.Equal(GatewayStateFault.Missing, Store.Read().Fault);
    }
}
