using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionCoordinatorTests : IDisposable
{
    private const string ApplicationId = "PFN:OpenClaw.Gateway_abc123";

    private readonly string _root = TestDirectory.Create();
    private readonly FakeMxcSessionClient _backend = new();
    private readonly List<string> _log = [];

    private string StatePath => Path.Combine(_root, "session.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SessionStateStore Store() => new(StatePath);

    private SessionCoordinator Create(
        ISessionLock? sessionLock = null,
        SessionStateStore? store = null,
        TimeSpan? lockTimeout = null) =>
        new(
            _backend,
            store ?? Store(),
            sessionLock ?? new AlwaysFreeLock(),
            ApplicationId,
            _log.Add,
            new FixedTimeProvider(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero)),
            lockTimeout);

    [Fact]
    public async Task FirstUseProvisionsAndStartsExactlyOnce()
    {
        SessionCoordinator coordinator = Create();

        SessionRecord record = await coordinator.EnsureStartedAsync(CancellationToken.None);

        Assert.Equal(["provision", "start:iso:sandbox1"], _backend.Calls);
        Assert.Equal("iso:sandbox1", record.SandboxId);
    }

    [Fact]
    public async Task FirstUseProvisionsWithThisInstallationsIdentity()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);

        Assert.Equal([ApplicationId], _backend.ProvisionedAppIds);
    }

    [Fact]
    public async Task StatusProbeReportsRunningOnlyAfterMxcAcceptsTheRecordedProvision()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();

        SessionStatus status = await Create().ProbeRecordedStatusAsync(CancellationToken.None);

        Assert.Equal(SessionAvailability.Running, status.Availability);
        Assert.Equal("iso:sandbox1", status.Record!.SandboxId);
        Assert.Equal(["start:iso:sandbox1"], _backend.Calls);
    }

    [Fact]
    public async Task StatusProbeReportsAStaleProvisionWithoutReplacingIt()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();
        _backend.StartFailure = new MxcException(MxcErrorCode.StaleId, "provision was not found");

        SessionStatus status = await Create().ProbeRecordedStatusAsync(CancellationToken.None);

        Assert.Equal(SessionAvailability.Stale, status.Availability);
        Assert.Equal("iso:sandbox1", status.Record!.SandboxId);
        Assert.Equal(["start:iso:sandbox1"], _backend.Calls);
        Assert.Equal("iso:sandbox1", Store().Read(ApplicationId).Record!.SandboxId);
    }

    [Fact]
    public async Task StatusProbeReportsAnUnavailableBackend()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();
        _backend.StartFailure = new MxcException(
            MxcErrorCode.RuntimeUnavailable,
            "MXC is unavailable");

        SessionStatus status = await Create().ProbeRecordedStatusAsync(CancellationToken.None);

        Assert.Equal(SessionAvailability.BackendUnavailable, status.Availability);
        Assert.Equal("MXC is unavailable", status.Detail);
        Assert.Equal(["start:iso:sandbox1"], _backend.Calls);
    }

    [Fact]
    public async Task StatusProbeReportsOtherBackendFailures()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();
        _backend.StartFailure = new MxcException(MxcErrorCode.BackendError, "MXC is unavailable");

        SessionStatus status = await Create().ProbeRecordedStatusAsync(CancellationToken.None);

        Assert.Equal(SessionAvailability.BackendError, status.Availability);
        Assert.Equal("MXC is unavailable", status.Detail);
        Assert.Equal(["start:iso:sandbox1"], _backend.Calls);
    }

    [Fact]
    public async Task ProvisionMetadataIsPersisted()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);

        SessionRecord record = Store().Read(ApplicationId).Record!;
        Assert.Equal("agent_1", record.AgentUserName);
        Assert.Equal("S-1-5-21-0-0-0-1001", record.AgentUserSid);
        Assert.Equal(@"C:\Users\agent_1\Shared", record.WorkspacePath);
        Assert.Equal(
            new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            record.CreatedUtc);
    }

    [Fact]
    public async Task OwnershipIsRecordedBeforeTheSessionIsStarted()
    {
        // A crash between provision and start must not leave a sandbox that
        // nothing claims; the backend cannot be asked which sandboxes are ours.
        _backend.StartFailure = new MxcException(MxcErrorCode.BackendError, "start failed");

        await Assert.ThrowsAsync<MxcException>(
            () => Create().EnsureStartedAsync(CancellationToken.None));

        Assert.True(Store().Read(ApplicationId).HasRecord);
    }

    [Fact]
    public async Task FailedProvisionRecordsNothing()
    {
        _backend.ProvisionFailure = new MxcException(
            MxcErrorCode.BackendError,
            "provision failed");

        await Assert.ThrowsAsync<MxcException>(
            () => Create().EnsureStartedAsync(CancellationToken.None));

        Assert.Equal(SessionStateFault.Missing, Store().Read(ApplicationId).Fault);
    }

    [Fact]
    public async Task SecondUseReusesTheRecordedSessionWithoutProvisioning()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();

        SessionRecord record = await Create().EnsureStartedAsync(CancellationToken.None);

        Assert.Equal(["start:iso:sandbox1"], _backend.Calls);
        Assert.Equal("iso:sandbox1", record.SandboxId);
    }

    [Fact]
    public async Task StaleRecordedProvisionIsReplacedOnlyAfterTheBackendReportsItMissing()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();
        _backend.StartFailureForSandbox = sandboxId => sandboxId.Value == "iso:sandbox1"
            ? new MxcException(MxcErrorCode.StaleId, "provision was not found")
            : null;

        SessionRecord record = await Create().EnsureStartedAsync(CancellationToken.None);

        Assert.Equal(
            ["start:iso:sandbox1", "provision", "start:iso:sandbox2"],
            _backend.Calls);
        Assert.Equal("iso:sandbox2", record.SandboxId);
        Assert.Equal("iso:sandbox2", Store().Read(ApplicationId).Record!.SandboxId);
        Assert.Equal([ApplicationId, ApplicationId], _backend.ProvisionedAppIds);
    }

    [Fact]
    public async Task StaleRecordedProvisionReplacementIdentifiesTheSupersededSession()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.StartFailureForSandbox = sandboxId => sandboxId.Value == "iso:sandbox1"
            ? new MxcException(MxcErrorCode.StaleId, "provision was not found")
            : null;

        SessionStartResult result = await Create()
            .EnsureStartedWithResultAsync(CancellationToken.None);

        Assert.Equal("iso:sandbox2", result.Record.SandboxId);
        Assert.Equal("iso:sandbox1", result.SupersededRecord!.SandboxId);
    }

    [Fact]
    public async Task FailedStaleProvisionRecoveryRetainsTheRecordedOwnership()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();
        _backend.StartFailureForSandbox = _ =>
            new MxcException(MxcErrorCode.StaleId, "provision was not found");
        _backend.ProvisionFailure = new MxcException(
            MxcErrorCode.BackendError,
            "replacement provision failed");

        await Assert.ThrowsAsync<MxcException>(
            () => Create().EnsureStartedAsync(CancellationToken.None));

        Assert.Equal(["start:iso:sandbox1", "provision"], _backend.Calls);
        Assert.Equal("iso:sandbox1", Store().Read(ApplicationId).Record!.SandboxId);
    }

    [Fact]
    public async Task SeparateCoordinatorsConvergeOnOneSession()
    {
        // Distinct instances stand in for distinct processes sharing the store.
        SessionRecord first = await Create().EnsureStartedAsync(CancellationToken.None);
        SessionRecord second = await Create().EnsureStartedAsync(CancellationToken.None);
        SessionRecord third = await Create().EnsureStartedAsync(CancellationToken.None);

        Assert.Equal(first.SandboxId, second.SandboxId);
        Assert.Equal(first.SandboxId, third.SandboxId);
        Assert.Single(_backend.Calls, call => call == "provision");
    }

    [Fact]
    public async Task ReuseStartsTheSessionBecauseItMayHaveBeenStopped()
    {
        // A stopped session fails execution with backend_error rather than
        // restarting itself, and liveness cannot be queried.
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();

        await Create().EnsureStartedAsync(CancellationToken.None);

        Assert.Contains("start:iso:sandbox1", _backend.Calls);
    }

    [Fact]
    public async Task UnreadableRecordIsNotReplacedBySilentProvisioning()
    {
        File.WriteAllText(StatePath, "{ not json");

        SessionStateException exception = await Assert.ThrowsAsync<SessionStateException>(
            () => Create().EnsureStartedAsync(CancellationToken.None));

        Assert.Equal(SessionStateFault.Unreadable, exception.Fault);
        Assert.DoesNotContain("provision", _backend.Calls);
    }

    [Fact]
    public async Task ForeignRecordIsNotAdopted()
    {
        Store().Write(new SessionRecord
        {
            SandboxId = "iso:other",
            ApplicationId = "PFN:Internal.Gateway_xyz",
        });

        SessionStateException exception = await Assert.ThrowsAsync<SessionStateException>(
            () => Create().EnsureStartedAsync(CancellationToken.None));

        Assert.Equal(SessionStateFault.ForeignIdentity, exception.Fault);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task BusyLockIsReportedRatherThanWaitingForever()
    {
        SessionCoordinator coordinator = Create(
            new NeverFreeLock(),
            lockTimeout: TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<SessionBusyException>(
            () => coordinator.EnsureStartedAsync(CancellationToken.None));

        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task LockIsReleasedAfterAFailedOperation()
    {
        var sessionLock = new AlwaysFreeLock();
        _backend.ProvisionFailure = new MxcException(MxcErrorCode.BackendError, "no");

        await Assert.ThrowsAsync<MxcException>(
            () => Create(sessionLock).EnsureStartedAsync(CancellationToken.None));

        Assert.Equal(0, sessionLock.HeldCount);
    }

    [Fact]
    public async Task LockIsNotHeldAfterASuccessfulOperation()
    {
        var sessionLock = new AlwaysFreeLock();

        await Create(sessionLock).EnsureStartedAsync(CancellationToken.None);

        Assert.Equal(0, sessionLock.HeldCount);
    }

    [Fact]
    public void StatusWithoutASessionReportsNone()
    {
        SessionStatus status = Create().GetRecordedStatus();

        Assert.Equal(SessionAvailability.None, status.Availability);
        Assert.Null(status.Record);
    }

    [Fact]
    public async Task StatusDoesNotProvisionOrContactTheBackend()
    {
        Create().GetRecordedStatus();

        Assert.Empty(_backend.Calls);
        Assert.False(File.Exists(StatePath));
        await Task.CompletedTask.ConfigureAwait(true);
    }

    [Fact]
    public async Task StatusReportsTheRecordedSession()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();

        SessionStatus status = Create().GetRecordedStatus();

        Assert.Equal(SessionAvailability.Recorded, status.Availability);
        Assert.Equal("iso:sandbox1", status.Record!.SandboxId);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public void StatusDistinguishesUnusableFromAbsent()
    {
        File.WriteAllText(StatePath, "{ not json");

        SessionStatus status = Create().GetRecordedStatus();

        Assert.Equal(SessionAvailability.Unusable, status.Availability);
        Assert.Equal(SessionStateFault.Unreadable, status.Fault);
        Assert.NotNull(status.Detail);
    }

    [Fact]
    public async Task StopRetainsTheRecordAndTheProfile()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();

        bool stopped = await Create().StopAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.Equal(["stop:iso:sandbox1"], _backend.Calls);
        Assert.True(Store().Read(ApplicationId).HasRecord);
    }

    [Fact]
    public async Task StopWithoutASessionDoesNothing()
    {
        bool stopped = await Create().StopAsync(CancellationToken.None);

        Assert.False(stopped);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task StopDoesNotDeprovision()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);

        await Create().StopAsync(CancellationToken.None);

        Assert.DoesNotContain("deprovision:iso:sandbox1", _backend.Calls);
    }

    [Fact]
    public async Task StoppedSessionIsStartedAgainOnNextUse()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        await Create().StopAsync(CancellationToken.None);
        _backend.Calls.Clear();

        await Create().EnsureStartedAsync(CancellationToken.None);

        Assert.Equal(["start:iso:sandbox1"], _backend.Calls);
    }

    [Fact]
    public async Task RemoveStopsBeforeDeprovisioning()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();

        SessionRemovalResult result = await Create().RemoveAsync(CancellationToken.None);

        Assert.True(result.Removed);
        Assert.Null(result.StopFailure);
        Assert.Equal(["stop:iso:sandbox1", "deprovision:iso:sandbox1"], _backend.Calls);
    }

    [Fact]
    public async Task RemoveForgetsTheSessionOnlyAfterDeprovisionSucceeds()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);

        await Create().RemoveAsync(CancellationToken.None);

        Assert.Equal(SessionStateFault.Missing, Store().Read(ApplicationId).Fault);
    }

    [Fact]
    public async Task FailedDeprovisionRetainsOwnership()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.DeprovisionFailure = new MxcException(
            MxcErrorCode.BackendError,
            "deprovision failed");

        await Assert.ThrowsAsync<MxcException>(
            () => Create().RemoveAsync(CancellationToken.None));

        Assert.True(Store().Read(ApplicationId).HasRecord);
    }

    [Fact]
    public async Task FailedStopStillDeprovisionsAndIsReported()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        _backend.StopFailure = new MxcException(MxcErrorCode.BackendError, "stop failed");
        _backend.Calls.Clear();

        SessionRemovalResult result = await Create().RemoveAsync(CancellationToken.None);

        Assert.True(result.Removed);
        Assert.Equal("stop failed", result.StopFailure);
        Assert.Contains("deprovision:iso:sandbox1", _backend.Calls);
        Assert.Equal(SessionStateFault.Missing, Store().Read(ApplicationId).Fault);
    }

    [Fact]
    public async Task RemoveWithoutASessionDoesNothing()
    {
        SessionRemovalResult result = await Create().RemoveAsync(CancellationToken.None);

        Assert.False(result.Removed);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task RemoveDoesNotTouchAnotherInstallationsSession()
    {
        Store().Write(new SessionRecord
        {
            SandboxId = "iso:other",
            ApplicationId = "PFN:Internal.Gateway_xyz",
        });

        await Assert.ThrowsAsync<SessionStateException>(
            () => Create().RemoveAsync(CancellationToken.None));

        Assert.Empty(_backend.Calls);
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public async Task RemoveRefusesARecordItCannotRead()
    {
        File.WriteAllText(StatePath, "{ not json");

        await Assert.ThrowsAsync<SessionStateException>(
            () => Create().RemoveAsync(CancellationToken.None));

        Assert.Empty(_backend.Calls);
        await Task.CompletedTask.ConfigureAwait(true);
    }

    [Fact]
    public void ForgetDiscardsAnUnreadableRecordWithoutContactingTheBackend()
    {
        File.WriteAllText(StatePath, "{ not json");

        Create().ForgetRecordedState();

        Assert.Equal(SessionStateFault.Missing, Store().Read(ApplicationId).Fault);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task NewSessionCanBeCreatedAfterRemoval()
    {
        await Create().EnsureStartedAsync(CancellationToken.None);
        await Create().RemoveAsync(CancellationToken.None);

        SessionRecord record = await Create().EnsureStartedAsync(CancellationToken.None);

        Assert.Equal("iso:sandbox2", record.SandboxId);
    }
}
