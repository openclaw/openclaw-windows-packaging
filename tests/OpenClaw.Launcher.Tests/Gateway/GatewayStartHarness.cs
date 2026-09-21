using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// A gateway controller wired to fixture-owned storage, a dictated guest, and a
/// clock the test controls, so gateway lifecycle operations can be exercised
/// without a real session, a real process, or real elapsed time.
/// </summary>
internal sealed class GatewayStartHarness : IDisposable
{
    private const string ApplicationId = "PFN:OpenClaw.Gateway_test";
    private const string SandboxId = "iso:sandbox1";

    private readonly string _root = TestDirectory.Create();

    public GatewayStartHarness()
    {
        Start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        Clock = new AdvancingTimeProvider(Start);
        new SessionStateStore(Path.Combine(_root, "session.json")).Write(new SessionRecord
        {
            ApplicationId = ApplicationId,
            SandboxId = SandboxId,
            WorkspacePath = _root,
            Generation = "test-generation",
            CreatedUtc = DateTimeOffset.UnixEpoch
        });
    }

    public FakeSessionGatewayClient Client { get; } = new();

    public AdvancingTimeProvider Clock { get; }

    public DateTimeOffset Start { get; }

    public AlwaysFreeLock LifecycleLock { get; } = new();

    public GatewayStateStore Store => new(Path.Combine(_root, "gateway.json"));

    public Task<GatewayStartResult> StartAsync(
        IProgress<GatewayStartProgress>? progress = null) =>
        CreateController().StartAsync("helper.exe", CancellationToken.None, progress);

    public Task<GatewayRestartResult> RestartAsync(
        IProgress<GatewayStartProgress>? progress = null) =>
        CreateController().RestartAsync("helper.exe", CancellationToken.None, progress);

    /// <summary>
    /// Records a gateway whose identity matches what the fake guest reports, so
    /// the controller treats it as one it already owns.
    /// </summary>
    public void RecordRunningGateway(bool autostartDisabled = false) =>
        Store.Write(new GatewayRecord
        {
            SandboxId = SandboxId,
            ProcessId = 1234,
            ProcessStartTimeUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            StatusPath = "status.json",
            AutostartDisabled = autostartDisabled
        });

    public void RecordPendingGateway() =>
        Store.Write(new GatewayRecord
        {
            SandboxId = SandboxId,
            LaunchPending = true,
            ProcessStartTimeUtc = Start,
            StartedUtc = Start
        });

    private GatewayController CreateController()
    {
        var sessionStore = new SessionStateStore(Path.Combine(_root, "session.json"));
        var sessions = new SessionCoordinator(
            new FakeMxcSessionClient(),
            sessionStore,
            new AlwaysFreeLock(),
            ApplicationId,
            _ => { });

        return new GatewayController(
            sessions,
            Client,
            Store,
            _ => Task.FromResult(new GatewayStartRequest(
                HelperPath: Path.Combine(_root, "openclaw-session-host.exe"),
                NodePath: @"C:\Program Files\nodejs\node.exe",
                ApplicationDirectory: Path.Combine(_root, "app"),
                Port: null)),
            _ => { },
            () => sessions.GetRecordedStatus().Record
                ?? throw new SessionException("Run setup."),
            LifecycleLock,
            Clock);
    }

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
}
