using System.ComponentModel;
using OpenClaw.SessionHost;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Exercises the listener ownership rule against arranged process trees, where
/// identifier reuse, unreadable processes, missing owners, and cycles can be
/// produced on demand.
/// </summary>
/// <remarks>
/// Inside the isolated session a process snapshot and every refused process
/// open each cost tens of milliseconds. The arranged session therefore fails a
/// test outright when an inspection takes a second snapshot, opens a process
/// twice, or opens a process whose parent chain never reaches the supervisor.
/// </remarks>
public sealed class ProcessTreeSnapshotTests
{
    private const int Idle = 0;
    private const int SystemProcess = 4;
    private const int Services = 500;
    private const int ServiceHost = 600;
    private const int SupervisorParent = 900;
    private const int Supervisor = 1000;
    private const int Gateway = 1100;
    private const int GatewayChild = 1200;
    private const int GatewayPort = 18789;

    private static readonly DateTimeOffset Boot = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADirectChildOfTheSupervisorOwnsItsListener()
    {
        Assert.Equal([GatewayPort], OwnedPorts(GatewaySession(), (GatewayPort, Gateway)));
    }

    [Fact]
    public void AChildThatStartedInTheSameTickAsItsParentIsStillOwned()
    {
        // Creation times are coarse, so a gateway the supervisor starts at
        // once can carry exactly the supervisor's timestamp.
        SyntheticSession session = GatewaySession().WithStartTime(Gateway, At(0));

        Assert.Equal([GatewayPort], OwnedPorts(session, (GatewayPort, Gateway)));
    }

    [Fact]
    public void AGrandchildOfTheSupervisorOwnsItsListener()
    {
        Assert.Equal([GatewayPort], OwnedPorts(GatewaySession(), (GatewayPort, GatewayChild)));
    }

    [Fact]
    public void TheSupervisorOwnsItsOwnListener()
    {
        Assert.Equal([GatewayPort], OwnedPorts(GatewaySession(), (GatewayPort, Supervisor)));
    }

    [Fact]
    public void AnUnrelatedOwnerIsNeverOpened()
    {
        // Each of these owners refuses to be opened. Opening them one listener
        // at a time is what made a single inspection take seconds.
        IReadOnlyList<int> owned = OwnedPorts(
            GatewaySession(),
            (135, ServiceHost),
            (445, SystemProcess),
            (GatewayPort, Gateway),
            (5040, ServiceHost));

        Assert.Equal([GatewayPort], owned);
    }

    [Theory]
    [InlineData(Gateway)]
    [InlineData(Supervisor)]
    public void AParentThatStartedAfterItsChildIsAReusedIdentifier(int reused)
    {
        // Once a parent exits, a later process can inherit its identifier;
        // only the creation order shows that it is not the real parent.
        SyntheticSession session = GatewaySession().WithStartTime(reused, At(60));

        Assert.Empty(OwnedPorts(session, (GatewayPort, GatewayChild)));
    }

    [Theory]
    [InlineData(GatewayChild)]
    [InlineData(Gateway)]
    [InlineData(Supervisor)]
    public void AnUnreadableStartTimeOnTheChainDeniesOwnership(int unreadable)
    {
        SyntheticSession session = GatewaySession().WithStartTime(unreadable, null);

        Assert.Empty(OwnedPorts(session, (GatewayPort, GatewayChild)));
    }

    [Fact]
    public void AnOwnerMissingFromTheSnapshotIsNotOwned()
    {
        // The owner exited between reading the listener table and taking the
        // snapshot.
        SyntheticSession session = GatewaySession().Without(GatewayChild);

        Assert.Empty(OwnedPorts(session, (GatewayPort, GatewayChild)));
    }

    [Theory]
    [InlineData(1300)]
    [InlineData(1301)]
    [InlineData(1302)]
    public void ACycleInTheSnapshotEndsAsNotOwned(int loopsBackTo)
    {
        // Identifier reuse can make parent links circular. With equal creation
        // times nothing but cycle detection can end the walk.
        SyntheticSession session = GatewaySession()
            .With(1300, 1301, At(5))
            .With(1301, 1302, At(5))
            .With(1302, loopsBackTo, At(5));

        Assert.Empty(OwnedPorts(session, (GatewayPort, 1300)));
    }

    [Fact]
    public void EachOwnedPortIsReportedOnceInAscendingOrder()
    {
        // One inspection judges the whole table from one snapshot: unsorted
        // rows, repeated rows, an unrelated owner sharing the gateway's port,
        // and listeners that belong to System.
        IReadOnlyList<int> owned = OwnedPorts(
            GatewaySession(),
            (18792, GatewayChild),
            (GatewayPort, ServiceHost),
            (GatewayPort, Gateway),
            (445, SystemProcess),
            (GatewayPort, Gateway),
            (18791, Gateway),
            (5000, Supervisor),
            (18791, Gateway),
            (135, ServiceHost));

        Assert.Equal([5000, GatewayPort, 18791, 18792], owned);
    }

    [Fact]
    public void NoSnapshotIsTakenWhenTheSupervisorOwnsEveryListener()
    {
        // A snapshot that would fail cannot fail an inspection that never
        // needs one.
        SyntheticSession session = GatewaySession().WithFailingSnapshot();

        Assert.Equal(
            [GatewayPort, 18790],
            OwnedPorts(session, (18790, Supervisor), (GatewayPort, Supervisor)));
    }

    [Fact]
    public void ASnapshotFailureFailsTheInspectionWhenAnotherProcessListens()
    {
        // Reporting the gateway's listener as unowned would call a healthy
        // gateway unhealthy, so the failure has to surface and leave the
        // inspection unknown.
        SyntheticSession session = GatewaySession().WithFailingSnapshot();

        Assert.Throws<Win32Exception>(
            () => OwnedPorts(session, (GatewayPort, Supervisor), (18790, Gateway)));
    }

    private static DateTimeOffset At(int seconds) => Boot.AddSeconds(seconds);

    private static IReadOnlyList<int> OwnedPorts(
        SyntheticSession session,
        params (int Port, int Owner)[] listeners) =>
        GuestProcessObserver.ListeningPortsOwnedBy(listeners, session.Observe(), Supervisor);

    /// <summary>
    /// The supervisor, its gateway, and the gateway's child, beside system
    /// processes that refuse to be opened. The supervisor's own parent refuses
    /// too, so a walk that continued past the supervisor would fail the test.
    /// </summary>
    private static SyntheticSession GatewaySession() =>
        new SyntheticSession()
            .WithUnrelated(Idle, Idle)
            .WithUnrelated(SystemProcess, Idle)
            .WithUnrelated(Services, SystemProcess)
            .WithUnrelated(ServiceHost, Services)
            .WithUnrelated(SupervisorParent, SystemProcess)
            .With(Supervisor, SupervisorParent, At(0))
            .With(Gateway, Supervisor, At(1))
            .With(GatewayChild, Gateway, At(2));

    /// <summary>
    /// A process table observed the way the isolated session is observed:
    /// through one snapshot and one creation-time query per process.
    /// </summary>
    private sealed class SyntheticSession
    {
        private const int ErrorAccessDenied = 5;

        private readonly Dictionary<int, int> _parents = [];
        private readonly Dictionary<int, DateTimeOffset?> _startTimes = [];
        private readonly HashSet<int> _unrelated = [];
        private readonly HashSet<int> _opened = [];
        private bool _snapshotFails;
        private bool _observed;

        public SyntheticSession With(int processId, int parentId, DateTimeOffset? startTimeUtc)
        {
            _parents[processId] = parentId;
            _startTimes[processId] = startTimeUtc;
            return this;
        }

        public SyntheticSession WithUnrelated(int processId, int parentId)
        {
            _parents[processId] = parentId;
            _unrelated.Add(processId);
            return this;
        }

        public SyntheticSession WithStartTime(int processId, DateTimeOffset? startTimeUtc)
        {
            _startTimes[processId] = startTimeUtc;
            return this;
        }

        public SyntheticSession Without(int processId)
        {
            _parents.Remove(processId);
            _startTimes.Remove(processId);
            return this;
        }

        public SyntheticSession WithFailingSnapshot()
        {
            _snapshotFails = true;
            return this;
        }

        public ProcessTreeSnapshot Observe() => new(ReadParents, ReadStartTime);

        private Dictionary<int, int> ReadParents()
        {
            if (_snapshotFails)
            {
                throw new Win32Exception(ErrorAccessDenied);
            }
            if (_observed)
            {
                Assert.Fail("An inspection took a second process snapshot.");
            }

            _observed = true;
            return _parents;
        }

        private DateTimeOffset? ReadStartTime(int processId)
        {
            if (_unrelated.Contains(processId))
            {
                Assert.Fail($"Process {processId} never reaches the supervisor but was opened.");
            }
            if (!_opened.Add(processId))
            {
                Assert.Fail($"Process {processId} was opened twice in one inspection.");
            }

            return _startTimes.GetValueOrDefault(processId);
        }
    }
}
