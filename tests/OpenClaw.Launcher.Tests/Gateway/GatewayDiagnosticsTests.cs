using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayDiagnosticsTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private readonly FakeMxcSessionClient _backend = new();
    private readonly FakeSessionGatewayClient _client = new();
    private readonly FakeGatewayTaskScheduler _scheduler = new();

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

    private HostPaths Paths => HostPaths.ForRoot(_root, "OpenClaw.Gateway_test");

    private GatewayRuntime CreateRuntime(string? packageFamilyName = "OpenClaw.Gateway_test")
    {
        HostPaths paths = HostPaths.ForRoot(_root, packageFamilyName);
        SessionRuntime session = SessionRuntime.Create(
            paths,
            () => throw new InvalidOperationException("No real runtime in tests."),
            _root,
            _ => { },
            _backend);

        return GatewayRuntime.Create(
            new HostOptions(Path.Combine(_root, "app"), []),
            paths,
            session,
            _root,
            _ => { },
            userSid: "S-1-5-21-1",
            scheduler: _scheduler,
            client: _client);
    }

    private Task<GatewayDiagnosticReport> CollectAsync(
        GatewayRuntime runtime,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null)
    {
        Func<CancellationToken, Task<NodeRuntime>> node = resolveNode ?? DefaultNode;

        return GatewayDiagnostics.CollectAsync(
            runtime,
            runtime.Paths,
            new HostOptions(Path.Combine(_root, "app"), []),
            runtime.Session.Coordinator,
            node,
            CancellationToken.None);
    }

    private static Task<NodeRuntime> DefaultNode(CancellationToken cancellationToken) =>
        Task.FromResult(
            new NodeRuntime(
                @"C:\Program Files\nodejs\node.exe",
                new Version(24, 15, 0),
                System.Runtime.InteropServices.Architecture.X64));

    private static GatewayDiagnostic Find(
        GatewayDiagnosticReport report,
        string name) =>
        report.Checks.Single(check => check.Name == name);

    [Fact]
    public async Task EveryLinkInTheChainIsReported()
    {
        // A failure anywhere reads as "the gateway isn't running", so each link
        // has to be named separately for the report to be worth anything.
        GatewayDiagnosticReport report = await CollectAsync(CreateRuntime());

        foreach (string name in new[]
        {
            "Package identity",
            "Packaged application",
            "Node.js",
            "Session helper",
            "Isolated session",
            "Launch configuration",
            "Sign-in launcher"
        })
        {
            Assert.Contains(report.Checks, check => check.Name == name);
        }
    }

    [Fact]
    public async Task AMissingApplicationIsNamedRatherThanBlamedOnTheGateway()
    {
        GatewayDiagnosticReport report = await CollectAsync(CreateRuntime());

        GatewayDiagnostic application = Find(report, "Packaged application");
        Assert.False(application.Ok);
        Assert.Contains("openclaw.mjs", application.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResolvableNodeIsReportedWithItsVersionAndPath()
    {
        GatewayDiagnosticReport report = await CollectAsync(CreateRuntime());

        GatewayDiagnostic node = Find(report, "Node.js");
        Assert.True(node.Ok);
        Assert.Contains("24.15.0", node.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingNodeIsReportedWithItsOwnMessage()
    {
        GatewayDiagnosticReport report = await CollectAsync(
            CreateRuntime(),
            _ => Task.FromException<NodeRuntime>(
                new InvalidOperationException("Node.js was not found on PATH.")));

        GatewayDiagnostic node = Find(report, "Node.js");
        Assert.False(node.Ok);
        Assert.Contains("not found", node.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentSessionIsUnknownRatherThanAFailure()
    {
        // Not having provisioned yet is the normal state before the first
        // start, not a problem to chase.
        GatewayDiagnosticReport report = await CollectAsync(CreateRuntime());

        Assert.Null(Find(report, "Isolated session").Ok);
    }

    [Fact]
    public async Task AnUnwrittenLauncherIsUnknownRatherThanAFailure()
    {
        Assert.Null(Find(await CollectAsync(CreateRuntime()), "Sign-in launcher").Ok);
    }

    [Fact]
    public async Task AForeignLauncherFileIsReportedAsAFailure()
    {
        // A file we did not generate is reported, never silently overwritten.
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.GatewayLauncherPath)!);
        await File.WriteAllTextAsync(
            Paths.GatewayLauncherPath,
            "@echo someone else's script",
            CancellationToken.None);

        GatewayDiagnostic launcher =
            Find(await CollectAsync(CreateRuntime()), "Sign-in launcher");

        Assert.False(launcher.Ok);
    }

    [Fact]
    public async Task AGeneratedLauncherIsReportedAsHealthy()
    {
        _client.Inspection = new SessionInspectResult
        {
            ProcessFound = true,
            StartTimeMatches = true,
            PortListening = true,
            ListenerOwned = true
        };
        GatewayRuntime runtime = CreateRuntime();
        await runtime.Persistence.InstallAsync(CancellationToken.None);

        Assert.True(Find(await CollectAsync(runtime), "Sign-in launcher").Ok);
    }

    [Fact]
    public async Task TheConfiguredPortIsShown()
    {
        GatewayRuntime runtime = CreateRuntime();
        runtime.Configuration.Write(new GatewayLaunchConfiguration { Port = 9100 });

        GatewayDiagnostic configuration =
            Find(await CollectAsync(runtime), "Launch configuration");

        Assert.True(configuration.Ok);
        Assert.Contains("9100", configuration.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableConfigurationIsReportedWithoutFailingTheReport()
    {
        GatewayRuntime runtime = CreateRuntime();
        await File.WriteAllTextAsync(
            Paths.GatewayConfigurationPath,
            "{ not json",
            CancellationToken.None);

        GatewayDiagnostic configuration =
            Find(await CollectAsync(runtime), "Launch configuration");

        Assert.False(configuration.Ok);
    }

    [Fact]
    public async Task DiagnosingChangesNothing()
    {
        GatewayRuntime runtime = CreateRuntime();

        await CollectAsync(runtime);

        Assert.Empty(_backend.Calls);
        Assert.Empty(_client.Calls);
        Assert.DoesNotContain(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));
        Assert.False(File.Exists(Paths.GatewayLauncherPath));
    }

    [Fact]
    public void UnknownIsRenderedDistinctlyFromFailure()
    {
        // Rendering unknown as a failure would send the user chasing a problem
        // that may not exist.
        using var writer = new StringWriter();
        ClawCtlConsole.WriteGatewayDiagnostics(
            writer,
            new GatewayDiagnosticReport(
                [
                    new GatewayDiagnostic("Healthy", true, "fine"),
                    new GatewayDiagnostic("Broken", false, "bad"),
                    new GatewayDiagnostic("Undetermined", null, "not yet")
                ],
                new GatewayStatusReport(GatewayState.NotStarted, null, "none"),
                new GatewayPersistenceStatus(
                    GatewayPersistenceState.NotInstalled,
                    GatewayPersistenceLane.None,
                    "not configured"),
                null));

        string text = writer.ToString();
        Assert.Contains("[ok  ] Healthy", text, StringComparison.Ordinal);
        Assert.Contains("[FAIL] Broken", text, StringComparison.Ordinal);
        Assert.Contains("[?   ] Undetermined", text, StringComparison.Ordinal);
    }
}
