using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Gateway;
using OpenClaw.Launcher.Tests.Session;

namespace OpenClaw.Launcher.Tests;

public sealed class ProgramTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();
    private readonly FakeMxcSessionClient _backend = new();
    private readonly FakeGatewayTaskScheduler _scheduler = new();

    [Fact]
    public async Task AgentLaunchResolvesNodeAndRunsPackagedApplication()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        string[] arguments = ["gateway", "run", "--port", "12345"];
        var options = new HostOptions(applicationDirectory, arguments);
        var nodeRuntime = new NodeRuntime(
            Path.Combine(_testDirectory, "node.exe"),
            new Version(24, 15, 0),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        bool nodeResolutionAttempted = false;
        bool launchAttempted = false;

        int exitCode = await Program.RunAgentAsync(
            options,
            _ => { },
            _ =>
            {
                nodeResolutionAttempted = true;
                return Task.FromResult(nodeRuntime);
            },
            (nodePath, appDirectory, forwardedArguments, _, _) =>
            {
                launchAttempted = true;
                Assert.Equal(nodeRuntime.ExecutablePath, nodePath);
                Assert.Equal(applicationDirectory, appDirectory);
                Assert.Equal(arguments, forwardedArguments);
                return Task.FromResult(23);
            },
            _ => Task.FromResult(
                new SessionRoutingDecision(
                    SessionRouting.Direct,
                    "this test routes directly")),
            (_, _, _, _, _) =>
                throw new InvalidOperationException(
                    "Direct routing must not enter a session."));

        Assert.True(nodeResolutionAttempted);
        Assert.True(launchAttempted);
        Assert.Equal(23, exitCode);
    }

    [Fact]
    public async Task SetupProvisionsSessionPersistsConfigurationAndEnablesSignInRecovery()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        string entryPoint = Path.Combine(applicationDirectory, "openclaw.mjs");
        await File.WriteAllTextAsync(entryPoint, "console.log('fixture');");
        DateTime lastWriteTime = File.GetLastWriteTimeUtc(entryPoint);
        var options = new HostOptions(applicationDirectory, []);
        var nodeRuntime = new NodeRuntime(
            Path.Combine(_testDirectory, "node.exe"),
            new Version(24, 15, 0),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            options,
            ["setup"],
            _ => { },
            output,
            TextWriter.Null,
            _ => Task.FromResult(nodeRuntime),
            createGatewayRuntime: CreateGatewayRuntime);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(entryPoint));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(entryPoint));
        Assert.Contains(
            nodeRuntime.ExecutablePath,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            applicationDirectory,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("setup is complete", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Gateway: not started.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(["provision", "start:iso:sandbox1"], _backend.Calls);
        Assert.Contains(
            _scheduler.Calls,
            call => call.StartsWith("register:", StringComparison.Ordinal));

        HostPaths paths = HostPaths.ForRoot(
            _testDirectory,
            "OpenClaw.Gateway_test");
        Assert.Equal(
            GatewayConfigurationStore.CurrentSchemaVersion,
            new GatewayConfigurationStore(paths.GatewayConfigurationPath)
                .Read()
                .Configuration!
                .SchemaVersion);
        Assert.Equal(
            GatewayStateFault.Missing,
            new GatewayStateStore(paths.GatewayStatePath).Read().Fault);
        Assert.NotNull(
            new SetupStateStore(paths.SetupStatePath)
                .Read("PFN:OpenClaw.Gateway_test")
                .Record);
    }

    [Fact]
    public async Task SetupDoesNotMarkTheInstallationWhenSignInRecoveryFails()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        _scheduler.Probe = GatewayTaskProbe.Unreadable("Access is denied.");

        int exitCode = await Program.RunControlAsync(
            new HostOptions(applicationDirectory, []),
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            _ => Task.FromResult(
                new NodeRuntime(
                    "node.exe",
                    new Version(24, 15, 0),
                    System.Runtime.InteropServices.RuntimeInformation
                        .ProcessArchitecture)),
            createGatewayRuntime: CreateGatewayRuntime);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            SetupStateFault.Missing,
            new SetupStateStore(
                HostPaths.ForRoot(
                    _testDirectory,
                    "OpenClaw.Gateway_test").SetupStatePath)
                .Read("PFN:OpenClaw.Gateway_test")
                .Fault);
    }

    [Fact]
    public async Task SetupResolvesNodeBeforeReportingMissingApplication()
    {
        bool nodeResolutionAttempted = false;

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.RunControlAsync(
            new HostOptions(null, []),
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            _ =>
            {
                nodeResolutionAttempted = true;
                return Task.FromResult(
                    new NodeRuntime(
                        "node.exe",
                        new Version(24, 15, 0),
                        System.Runtime.InteropServices.RuntimeInformation
                            .ProcessArchitecture));
            }));

        Assert.True(nodeResolutionAttempted);
    }

    [Fact]
    public async Task AgentResolvesNodeBeforeReportingMissingApplication()
    {
        bool nodeResolutionAttempted = false;

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.RunAgentAsync(
            new HostOptions(null, []),
            _ => { },
            _ =>
            {
                nodeResolutionAttempted = true;
                return Task.FromResult(
                    new NodeRuntime(
                        "node.exe",
                        new Version(24, 15, 0),
                        System.Runtime.InteropServices.RuntimeInformation
                            .ProcessArchitecture));
            },
            (_, _, _, _, _) => Task.FromResult(23),
            _ => Task.FromResult(
                new SessionRoutingDecision(
                    SessionRouting.Direct,
                    "test direct execution")),
            (_, _, _, _, _) => throw new InvalidOperationException(
                "Direct routing must not enter a session.")));

        Assert.True(nodeResolutionAttempted);
    }

    private GatewayRuntime CreateGatewayRuntime(
        HostOptions options,
        Action<string> log)
    {
        string helperPath = SessionRuntime.ResolveHelperPath(_testDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, "test helper");
        string workspace = Path.Combine(_testDirectory, "workspace");
        Directory.CreateDirectory(workspace);
        _backend.Metadata = new MxcProvisionMetadata(
            "agent_1",
            "S-1-5-21-0-0-0-1001",
            workspace);

        HostPaths paths = HostPaths.ForRoot(
            _testDirectory,
            "OpenClaw.Gateway_test");
        SessionRuntime session = SessionRuntime.Create(
            paths,
            () => throw new InvalidOperationException(
                "The setup test must not locate a real MXC runtime."),
            _testDirectory,
            log,
            _backend);

        return GatewayRuntime.Create(
            options,
            paths,
            session,
            _testDirectory,
            log,
            userSid: "S-1-5-21-1",
            scheduler: _scheduler);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
