using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayLaunchAttemptTests
{
    private const string Attempt = "1234567890abcdef1234567890abcdef";
    private const string OtherAttempt = "abcdef1234567890abcdef1234567890";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartingOrRunningLaunchIsAdoptedWithoutTransferringCleanupOwnership(bool listening)
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = Live(listening);
        GatewayStartResult created = await harness.StartAsync(attemptId: Attempt);
        GatewayStartResult adopted = await harness.StartAsync(attemptId: OtherAttempt);

        Assert.True(created.Created);
        Assert.False(adopted.Created);
        Assert.Equal(Attempt, adopted.Record.AttemptId);
        Assert.Equal(created.State, adopted.State);
        Assert.Single(harness.Client.Calls, call => call == "start");
        harness.Client.Calls.Clear();

        GatewayStopResult refused = await harness.StopAsync(OtherAttempt);
        Assert.False(refused.Succeeded);
        Assert.Equal("attempt-mismatch", refused.Refusal);
        Assert.Empty(harness.Client.Calls);
        Assert.Equal(Attempt, harness.Store.Read().Record!.AttemptId);

        GatewayStopResult stopped = await harness.StopAsync(Attempt);
        Assert.True(stopped.Stopped);
        Assert.Equal(Attempt, stopped.AttemptId);
    }

    [Fact]
    public async Task CleanupCannotStopAReplacementStartedByAnotherCaller()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = Live(true);
        await harness.StartAsync(attemptId: Attempt);
        await harness.StopAsync();
        await harness.StartAsync(attemptId: OtherAttempt);
        harness.Client.Calls.Clear();

        GatewayStopResult result = await harness.StopAsync(Attempt);

        Assert.False(result.Succeeded);
        Assert.Equal("attempt-mismatch", result.Refusal);
        Assert.Empty(harness.Client.Calls);
        Assert.Equal(OtherAttempt, harness.Store.Read().Record!.AttemptId);
    }

    [Fact]
    public async Task MatchingAttemptWithoutInspectionDoesNotFallBackToUnconditionalStop()
    {
        using var harness = new GatewayStartHarness();
        harness.Client.Inspection = Live(true);
        await harness.StartAsync(attemptId: Attempt);
        harness.Client.Inspection = new SessionInspectResult { Error = "unavailable" };
        harness.Client.Calls.Clear();

        GatewayStopResult result = await harness.StopAsync(Attempt);

        Assert.False(result.Succeeded);
        Assert.Equal("ownership-unverified", result.Refusal);
        Assert.DoesNotContain(harness.Client.Calls, call => call.StartsWith("stop:", StringComparison.Ordinal));
        Assert.Equal(Attempt, harness.Store.Read().Record!.AttemptId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-attempt")]
    [InlineData("wrong-helper")]
    [InlineData("wrong-schema")]
    [InlineData("missing-identity")]
    [InlineData("invalid-json")]
    [InlineData("valid")]
    public async Task LostAcknowledgementRecoversOnlyFromTheExactDurableSupervisorReceipt(string receiptKind)
    {
        string root = TestDirectory.Create();
        try
        {
            string helper = Path.Combine(root, "packaged-helper.exe");
            File.WriteAllText(helper, "fixture");
            string stagedHelper = SessionHelperStager.Stage(helper, root);
            var session = new SessionRecord
            {
                SandboxId = "iso:fixture",
                ApplicationId = "PFN:fixture",
                WorkspacePath = root,
                Generation = "fixture",
                CreatedUtc = DateTimeOffset.UnixEpoch
            };
            var sessionsStore = new SessionStateStore(Path.Combine(root, "session.json"));
            sessionsStore.Write(session);
            var store = new GatewayStateStore(Path.Combine(root, "gateway.json"));
            var backend = new FakeMxcSessionClient();
            var sessions = new SessionCoordinator(backend, sessionsStore, new AlwaysFreeLock(), session.ApplicationId, _ => { });
            var client = new SessionGatewayClient(backend, _ => { });
            var controller = new GatewayController(sessions, client, store,
                _ => Task.FromResult(new GatewayStartRequest(helper, @"C:\fixture\node.exe", root, null)),
                _ => { }, () => session, new AlwaysFreeLock(), _ => { });
            string? statusPath = null;
            backend.ExecuteBehavior = _ =>
            {
                GatewayRecord intent = store.Read().Record!;
                Assert.Equal(Attempt, intent.AttemptId);
                Assert.True(intent.LaunchPending);
                string path = Directory.GetFiles(root, "gateway-*.json").Single();
                SessionLaunchRequest launch = SessionLaunchProtocol.ReadRequest(File.ReadAllText(path));
                Assert.Equal(Attempt, launch.RequestId);
                statusPath = launch.StatusPath;
                throw new IOException("The start acknowledgement was lost.");
            };

            GatewayStartResult uncertain = await controller.StartAsync(helper, CancellationToken.None, attemptId: Attempt);
            Assert.Equal(GatewayState.Unknown, uncertain.State);
            Assert.True(uncertain.Created);
            Assert.Equal(Attempt, uncertain.Record.AttemptId);
            string intentBefore = File.ReadAllText(Path.Combine(root, "gateway.json"));

            GatewayStatusReport unanswered = await controller.GetStatusAsync(helper, CancellationToken.None);
            Assert.Equal(GatewayState.Unknown, unanswered.State);
            Assert.Equal(Attempt, unanswered.Record!.AttemptId);

            var receipt = new SessionSupervisorStatus
            {
                RequestId = receiptKind == "wrong-attempt" ? OtherAttempt : Attempt,
                SchemaVersion = receiptKind == "wrong-schema" ? 999 : SessionLaunchProtocol.CurrentSchemaVersion,
                ProcessId = 1234,
                ProcessStartTimeUtc = receiptKind == "missing-identity" ? default : DateTimeOffset.UnixEpoch,
                HelperPath = receiptKind == "wrong-helper" ? helper : stagedHelper,
                State = SessionSupervisorStatus.RunningState
            };
            if (receiptKind != "missing")
            {
                File.WriteAllText(statusPath!, receiptKind == "invalid-json"
                    ? "{ invalid" : SessionInspectProtocol.SerializeStatus(receipt));
            }
            int stops = 0;
            backend.ExecuteBehavior = request =>
            {
                string path = Directory.GetFiles(root, "inspect-*.json").Single();
                SessionInspectRequest inspection = SessionInspectProtocol.ReadRequest(File.ReadAllText(path));
                Assert.Equal(1234, inspection.ProcessId);
                Assert.Equal(DateTimeOffset.UnixEpoch, inspection.ProcessStartTimeUtc);
                Assert.Equal(stagedHelper, inspection.HelperPath);
                Assert.False(inspection.LaunchPending);
                bool stopping = request.CommandLine.Contains("--stop", StringComparison.Ordinal);
                if (stopping)
                {
                    stops++;
                }
                File.WriteAllText(SessionLaunchProtocol.ResultPathFor(path),
                    SessionInspectProtocol.SerializeResult(stopping
                        ? new SessionInspectResult { RequestId = inspection.RequestId }
                        : Live(true) with { RequestId = inspection.RequestId }));
                return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
            };

            GatewayStatusReport status = await controller.GetStatusAsync(helper, CancellationToken.None);
            Assert.Equal(receiptKind == "valid" ? GatewayState.Running : GatewayState.Unknown, status.State);
            Assert.Equal(intentBefore, File.ReadAllText(Path.Combine(root, "gateway.json")));
            GatewayStartResult adopted = await controller.StartAsync(helper, CancellationToken.None, attemptId: OtherAttempt);
            Assert.False(adopted.Created);
            Assert.Equal(Attempt, adopted.Record.AttemptId);
            GatewayStopResult stop = await controller.StopAsync(helper, CancellationToken.None, Attempt);
            Assert.Equal(receiptKind == "valid", stop.Succeeded);
            Assert.Equal(receiptKind == "valid" ? 1 : 0, stops);
            Assert.Equal(receiptKind == "valid" ? GatewayStateFault.Missing : null, store.Read().Fault);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SessionInspectResult Live(bool listening) => new()
    {
        ProcessFound = true,
        StartTimeMatches = true,
        ListenerOwned = listening,
        PortListening = listening,
        SupervisorState = SessionSupervisorStatus.RunningState,
        ListeningPorts = listening ? [12345] : []
    };
}
