using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests;

public sealed class ProgramTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public async Task AgentLaunchResolvesNodeAndRunsPackagedApplication()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        string[] arguments = ["gateway", "run", "--port", "12345"];
        var options = new HostOptions(applicationDirectory, null, arguments);
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
            });

        Assert.True(nodeResolutionAttempted);
        Assert.True(launchAttempted);
        Assert.Equal(23, exitCode);
    }

    [Fact]
    public async Task SetupPreparesNodeAndChecksPackagedApplication()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        string entryPoint = Path.Combine(applicationDirectory, "openclaw.mjs");
        await File.WriteAllTextAsync(entryPoint, "console.log('fixture');");
        string archivePath = Path.Combine(_testDirectory, "node-v24.15.0-win-x64.zip");
        await File.WriteAllTextAsync(archivePath, "fixture");
        DateTime lastWriteTime = File.GetLastWriteTimeUtc(entryPoint);
        var options = new HostOptions(applicationDirectory, archivePath, []);
        var nodeRuntime = new NodeRuntime(
            Path.Combine(_testDirectory, "node.exe"),
            new Version(24, 15, 0),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        using var output = new StringWriter();
        SessionRuntime runtime = CreateSessionRuntime();

        int exitCode = await Program.RunControlAsync(
            options,
            ["setup"],
            _ => { },
            output,
            TextWriter.Null,
            _ => Task.FromResult(nodeRuntime),
            () => runtime);

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
        SetupRecord setup = runtime.SetupState.Read(runtime.ApplicationId).Record!;
        Assert.Equal(SetupPhase.Ready, setup.Phase);
        Assert.False(setup.StartupEnabled);
        Assert.Equal("24.15.0", setup.AgentNodeVersion);
    }

    [Fact]
    public async Task SetupResolvesNodeBeforeReportingMissingApplication()
    {
        bool nodeResolutionAttempted = false;

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.RunControlAsync(
            new HostOptions(null, null, []),
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
    public async Task TeardownClearsPendingGatewayStateAfterSessionRemoval()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        runtime.GatewayState.Write(new GatewayRecord
        {
            SchemaVersion = GatewayStateStore.CurrentSchemaVersion,
            SandboxId = "iso:pending",
            LaunchPending = true,
            ProcessStartTimeUtc = DateTimeOffset.UtcNow
        });

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []),
            ["teardown"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            createSessionRuntime: () => runtime);

        Assert.Equal(0, exitCode);
        GatewayStateResult state = runtime.GatewayState.Read();
        Assert.Equal(GatewayStateFault.Missing, state.Fault);
    }

    [Fact]
    public async Task AgentResolvesNodeBeforeReportingMissingApplication()
    {
        bool nodeResolutionAttempted = false;

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.RunAgentAsync(
            new HostOptions(null, null, []),
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
            }));

        Assert.True(nodeResolutionAttempted);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private SessionRuntime CreateSessionRuntime()
    {
        string stateRoot = Path.Combine(_testDirectory, "state");
        string baseDirectory = Path.Combine(_testDirectory, "base");
        string workspace = Path.Combine(_testDirectory, "workspace");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(workspace);
        string helperPath = SessionRuntime.ResolveHelperPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, "fixture");
        var backend = new FakeMxcSessionClient
        {
            Metadata = new MxcProvisionMetadata(
                "agent_1",
                "S-1-5-21-0-0-0-1001",
                workspace)
        };
        backend.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(workspace, "runtime-*.json").Single();
            SessionRuntimeInstallRequest request = SessionRuntimeProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionRuntimeProtocol.SerializeResult(new SessionRuntimeInstallResult
                {
                    RequestId = request.RequestId,
                    ExecutablePath = Path.Combine(
                        workspace,
                        "AppData",
                        "Local",
                        "OpenClaw",
                        "NodeJS",
                        "node-v24.15.0-win-x64",
                        "node.exe"),
                    Version = "24.15.0",
                    ArchiveName = "node-v24.15.0-win-x64.zip"
                }));
            return Task.FromResult(0);
        };
        return SessionRuntime.Create(
            HostPaths.ForRoot(stateRoot, "OpenClaw.Gateway_test"),
            () => throw new InvalidOperationException("The test supplies its backend."),
            baseDirectory,
            _ => { },
            backend);
    }
}
