using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class ClawCtlSessionCommandTests : IDisposable
{
    private const string ApplicationId = "PFN:OpenClaw.Gateway_abc123";

    private readonly string _root = TestDirectory.Create();
    private readonly FakeMxcSessionClient _backend = new();
    private readonly List<string> _log = [];

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SessionCoordinator CreateCoordinator() =>
        new(
            _backend,
            new SessionStateStore(Path.Combine(_root, "session.json")),
            new AlwaysFreeLock(),
            ApplicationId,
            _log.Add,
            new FixedTimeProvider(
                new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero)));

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        SessionCoordinator coordinator = CreateCoordinator();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, []),
            args,
            _log.Add,
            output,
            error,
            createSessionCoordinator: _ => coordinator).ConfigureAwait(true);

        return (exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task StatusReportsNoSessionWithoutProvisioning()
    {
        (int exitCode, string output, _) = await RunAsync("session", "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("No isolated session is recorded", output, StringComparison.Ordinal);

        // The decisive assertion: status is read-only, so a machine with no
        // session must still have none afterwards.
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task StatusReportsTheRecordedSessionWithoutQueryingTheBackend()
    {
        SessionCoordinator coordinator = CreateCoordinator();
        await coordinator.EnsureStartedAsync(CancellationToken.None);
        _backend.Calls.Clear();

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, []),
            ["session", "status"],
            _log.Add,
            output,
            error,
            createSessionCoordinator: _ => coordinator).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Contains("An isolated session is recorded", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Live state is not queried", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task StopLeavesTheSessionProvisioned()
    {
        SessionCoordinator coordinator = CreateCoordinator();
        await coordinator.EnsureStartedAsync(CancellationToken.None);

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, []),
            ["session", "stop"],
            _log.Add,
            output,
            error,
            createSessionCoordinator: _ => coordinator).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Contains("Stopped the isolated session", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("profile and data are kept", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_backend.Calls, call => call.StartsWith("deprovision:", StringComparison.Ordinal));
        Assert.Equal(
            SessionAvailability.Recorded,
            coordinator.GetRecordedStatus().Availability);
    }

    [Fact]
    public async Task StopWithNoSessionSaysSoInsteadOfFailing()
    {
        (int exitCode, string output, _) = await RunAsync("session", "stop");

        Assert.Equal(0, exitCode);
        Assert.Contains("nothing to stop", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveWarnsThatGuestDataIsDestroyed()
    {
        SessionCoordinator coordinator = CreateCoordinator();
        await coordinator.EnsureStartedAsync(CancellationToken.None);

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, []),
            ["session", "remove"],
            _log.Add,
            output,
            error,
            createSessionCoordinator: _ => coordinator).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Contains("Removed the isolated session", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("guest profile and shared workspace contents are gone", output.ToString(), StringComparison.Ordinal);
        Assert.Single(_backend.Calls, call => call.StartsWith("deprovision:", StringComparison.Ordinal));
        Assert.Equal(
            SessionAvailability.None,
            coordinator.GetRecordedStatus().Availability);
    }

    [Fact]
    public async Task RemoveReportsAStopFailureRatherThanClaimingACleanTeardown()
    {
        SessionCoordinator coordinator = CreateCoordinator();
        await coordinator.EnsureStartedAsync(CancellationToken.None);
        _backend.StopFailure =
            new MxcException(MxcErrorCode.BackendError, "stop refused");

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, []),
            ["session", "remove"],
            _log.Add,
            output,
            error,
            createSessionCoordinator: _ => coordinator).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Contains("Warning: stopping the session failed", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Removed the isolated session", output.ToString(), StringComparison.Ordinal);
        Assert.Single(_backend.Calls, call => call.StartsWith("deprovision:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("session|stat")]
    [InlineData("session|status|extra")]
    public async Task MalformedSessionCommandsFailWithUsageInsteadOfActing(
        string packedArguments)
    {
        string[] args = packedArguments.Split('|');

        (int exitCode, _, string error) = await RunAsync(args);

        Assert.NotEqual(0, exitCode);
        Assert.NotEmpty(error);
        Assert.Empty(_backend.Calls);
    }

    // A bare noun is a discovery request, exactly as bare `clawctl` is. What
    // matters is that it lists the sub-commands rather than choosing one of
    // them, so a mistyped destructive verb can never resolve to an operation
    // the user did not type.
    [Fact]
    public async Task ABareSessionNounListsItsSubCommandsWithoutActing()
    {
        (int exitCode, string output, _) = await RunAsync("session");

        Assert.Equal(0, exitCode);
        Assert.Empty(_backend.Calls);
        foreach (string verb in new[] { "status", "stop", "remove" })
        {
            Assert.Contains(verb, output, StringComparison.Ordinal);
        }
    }
}
