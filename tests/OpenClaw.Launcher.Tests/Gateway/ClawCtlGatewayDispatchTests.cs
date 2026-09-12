using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// Drives the real command dispatch, so the commands are proven reachable
/// rather than merely present.
/// </summary>
public sealed class ClawCtlGatewayDispatchTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private readonly FakeMxcSessionClient _backend = new();
    private readonly FakeSessionGatewayClient _client = new();
    private readonly FakeGatewayTaskScheduler _scheduler = new();
    private readonly List<string> _log = [];

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

    private GatewayRuntime CreateRuntime(HostOptions options, Action<string> log)
    {
        HostPaths paths = HostPaths.ForRoot(_root, "OpenClaw.Gateway_test");
        SessionRuntime session = SessionRuntime.Create(
            paths,
            () => throw new InvalidOperationException(
                "The gateway tests must not locate a real MXC runtime."),
            _root,
            log,
            _backend);

        return GatewayRuntime.Create(
            options,
            paths,
            session,
            _root,
            log,
            userSid: "S-1-5-21-1",
            scheduler: _scheduler,
            client: _client);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(Path.Combine(_root, "app"), []),
            args,
            _log.Add,
            output,
            error,
            createGatewayRuntime: CreateRuntime).ConfigureAwait(true);

        return (exitCode, output.ToString(), error.ToString());
    }

    private static SessionInspectResult Healthy() => new()
    {
        ProcessFound = true,
        StartTimeMatches = true,
        PortListening = true,
        ListenerOwned = true
    };

    [Fact]
    public async Task StatusIsReadOnly()
    {
        (int exitCode, string output, _) = await RunAsync("gateway-service", "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("No gateway has been started", output, StringComparison.Ordinal);

        // Nothing is started, provisioned, or registered by asking.
        Assert.Empty(_backend.Calls);
        Assert.Empty(_client.Calls);
        Assert.DoesNotContain(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallStartsTheGatewayAndConfiguresSignInRecovery()
    {
        _client.Inspection = Healthy();

        (int exitCode, string output, _) = await RunAsync("gateway-service", "install");

        Assert.Equal(0, exitCode);
        Assert.Contains("start", _client.Calls);
        Assert.Contains(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
        Assert.Contains("Sign-in recovery", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusReportsARunningGatewayAfterInstall()
    {
        _client.Inspection = Healthy();
        await RunAsync("gateway-service", "install");
        _scheduler.Probe = GatewayTaskProbe.Present(
            GatewayTaskDefinition.CreateSnapshot(
                "S-1-5-21-1",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe"),
                HostPaths.ForRoot(_root, "OpenClaw.Gateway_test").GatewayLauncherPath));

        (int exitCode, string output, _) = await RunAsync("gateway-service", "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("running", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sign-in recovery: configured", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartIsIdempotent()
    {
        _client.Inspection = Healthy();
        await RunAsync("gateway-service", "install");

        (int exitCode, string output, _) = await RunAsync("gateway-service", "start");

        Assert.Equal(0, exitCode);
        Assert.Contains("already running", output, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_client.Calls, call => call == "start");
    }

    [Fact]
    public async Task StopEndsTheGatewayButKeepsTheSession()
    {
        _client.Inspection = Healthy();
        await RunAsync("gateway-service", "install");

        (int exitCode, string output, _) = await RunAsync("gateway-service", "stop");

        Assert.Equal(0, exitCode);
        Assert.Contains("stopped", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            _backend.Calls,
            call => call.StartsWith("deprovision:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UninstallStopsTheGatewayBeforeRemovingRecovery()
    {
        // Removing the task while the gateway is still running would leave a
        // process that nothing is recorded as owning.
        _client.Inspection = Healthy();
        await RunAsync("gateway-service", "install");
        _client.Calls.Clear();
        _scheduler.Calls.Clear();

        (int exitCode, string output, _) = await RunAsync("gateway-service", "uninstall");

        Assert.Equal(0, exitCode);
        int stopIndex = _client.Calls.FindIndex(
            call => call.StartsWith("stop:", StringComparison.Ordinal));
        int deleteIndex = _scheduler.Calls.FindIndex(
            call => call.StartsWith("delete:", StringComparison.Ordinal));
        Assert.True(stopIndex >= 0, "The gateway was not stopped.");
        Assert.True(deleteIndex >= 0, "Sign-in recovery was not removed.");
        Assert.Contains("session remove", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHalfConfiguredInstallDoesNotReportSuccess()
    {
        // Startup succeeds only when both the gateway and its sign-in recovery
        // succeed.
        _client.Inspection = Healthy();
        _scheduler.Probe = GatewayTaskProbe.Unreadable("Access is denied.");

        (int exitCode, _, _) = await RunAsync("gateway-service", "install");

        Assert.Equal(1, exitCode);
    }

    // A bare noun is a discovery request, exactly as bare `clawctl` is. The
    // property that matters is that it lists the sub-commands rather than
    // performing one.
    [Fact]
    public async Task ABareNounListsItsSubCommandsAndChangesNothing()
    {
        (int exitCode, string output, _) = await RunAsync("gateway-service");

        Assert.Equal(0, exitCode);
        Assert.Empty(_client.Calls);
        Assert.Empty(_scheduler.Calls);
        foreach (string verb in
            new[] { "install", "status", "start", "stop", "uninstall", "diagnose" })
        {
            Assert.Contains(verb, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DiagnoseSurvivesTheFailuresItExistsToExplain()
    {
        // Diagnose is what a user runs when something is already wrong. It must
        // report a broken first link rather than refuse to run because of it.
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(Path.Combine(_root, "app"), []),
            ["gateway-service", "diagnose"],
            _log.Add,
            output,
            error,
            createGatewayRuntime: (_, _) => throw new SessionException(
                "OpenClaw is not running from its installed package."),
            resolveNode: _ => throw new InvalidOperationException(
                "Node must not be resolved once the stack is unavailable."));

        Assert.Equal(1, exitCode);
        Assert.Contains("Gateway diagnostics", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "not running from its installed package",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnoseChangesNothing()
    {
        (int exitCode, string text, _) = await RunAsync("gateway-service", "diagnose");

        Assert.Equal(0, exitCode);
        Assert.Contains("Gateway diagnostics", text, StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
        Assert.Empty(_client.Calls);
        Assert.DoesNotContain(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnobservableGatewayIsNotReplaced()
    {
        _client.Inspection = Healthy();
        await RunAsync("gateway-service", "install");
        _client.Inspection = new SessionInspectResult { Error = "no answer" };

        await Assert.ThrowsAsync<SessionException>(
            () => RunAsync("gateway-service", "start"));
        Assert.Single(_client.Calls, call => call == "start");
    }
}
