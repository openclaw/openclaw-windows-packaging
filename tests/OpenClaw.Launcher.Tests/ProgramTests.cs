using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests;

public sealed class ProgramTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();
    private FakeMxcSessionClient? _lastSessionBackend;
    private string? _sessionStatePath;

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
            (
                nodePath,
                appDirectory,
                forwardedArguments,
                gatewayIsolationMode,
                _,
                _) =>
            {
                launchAttempted = true;
                Assert.Equal(nodeRuntime.ExecutablePath, nodePath);
                Assert.Equal(applicationDirectory, appDirectory);
                Assert.Equal(arguments, forwardedArguments);
                Assert.Equal(
                    GatewayIsolationMode.Disabled,
                    gatewayIsolationMode);
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
        Assert.Contains(
            _lastSessionBackend!.Calls,
            call => call.StartsWith("execute:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            _lastSessionBackend.Calls,
            call => call.StartsWith("execute-attached:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentUsesTheRuntimeInstalledForTheSessionWithoutHostFallback()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        string archivePath = Path.Combine(_testDirectory, "node-v24.15.0-win-x64.zip");
        await File.WriteAllTextAsync(archivePath, "fixture");
        var options = new HostOptions(applicationDirectory, archivePath, ["gateway"]);
        var hostNode = new NodeRuntime(
            Path.Combine(_testDirectory, "host-node.exe"),
            new Version(24, 15, 0),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        SessionRuntime runtime = CreateSessionRuntime();

        int setupExitCode = await Program.RunControlAsync(
            options,
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            _ => Task.FromResult(hostNode),
            () => runtime);
        Assert.Equal(0, setupExitCode);

        string expectedAgentNode = runtime.SetupState
            .Read(runtime.ApplicationId).Record!.AgentNodePath!;
        bool directLaunchAttempted = false;
        _lastSessionBackend!.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                "launch-*.json").Single();
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            Assert.Equal(expectedAgentNode, request.Executable);
            Assert.Equal(
                _lastSessionBackend.Metadata!.EphemeralWorkspacePath,
                request.WorkingDirectory);
            Assert.Equal(
                "enabled",
                request.Environment![OpenClawRuntimeEnvironment.GatewayIsolationVariable]);
            return Task.FromResult(0);
        };

        await Assert.ThrowsAsync<SessionException>(() => Program.RunAgentAsync(
            options,
            _ => { },
            _ => throw new InvalidOperationException("Host Node must not be resolved."),
            (_, _, _, _, _, _) =>
            {
                directLaunchAttempted = true;
                return Task.FromResult(0);
            },
            _ => runtime));

        Assert.False(directLaunchAttempted);
    }

    [Fact]
    public async Task AgentRefusesForeignSessionRecordWithoutHostFallback()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        var directLaunches = new List<string>();
        SessionRecord record = new SessionStateStore(_sessionStatePath!)
            .Read(runtime.ApplicationId).Record!;
        new SessionStateStore(_sessionStatePath!).Write(record with
        {
            SandboxId = SandboxIdFor("PFN:Some.Other.App_abc123")
        });

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentWithDirectLaunchProbeAsync(runtime, directLaunches));

        Assert.Contains("Some.Other.App", failure.Message, StringComparison.Ordinal);
        Assert.Empty(directLaunches);
    }

    [Fact]
    public async Task AutomaticDirectRoutingValidatesExistingOwnershipFirst()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        var directLaunches = new List<string>();
        SessionRecord record = new SessionStateStore(_sessionStatePath!)
            .Read(runtime.ApplicationId).Record!;
        new SessionStateStore(_sessionStatePath!).Write(record with
        {
            SandboxId = SandboxIdFor("PFN:Some.Other.App_abc123")
        });

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => Program.RunAgentAsync(
                new HostOptions(null, null, ["--version"]),
                _ => { },
                _ => throw new InvalidOperationException("Host Node must not be resolved."),
                (_, _, _, _, _, _) =>
                {
                    directLaunches.Add("direct");
                    return Task.FromResult(0);
                },
                _ => runtime,
                probeReadiness: _ => Task.FromResult(UnavailableReadiness()),
                getPackageFamilyName: () => "OpenClaw.Gateway_test"));

        Assert.Contains("Some.Other.App", failure.Message, StringComparison.Ordinal);
        Assert.Empty(directLaunches);
    }

    [Fact]
    public async Task AgentRefusesSetupSessionMismatchWithoutHostFallback()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        var directLaunches = new List<string>();
        SetupRecord setup = runtime.SetupState.Read(runtime.ApplicationId).Record!;
        runtime.SetupState.Write(setup with { SandboxId = "iso:different" });

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentWithDirectLaunchProbeAsync(runtime, directLaunches));

        Assert.Contains("different session", failure.Message, StringComparison.Ordinal);
        Assert.Empty(directLaunches);
    }

    [Fact]
    public async Task AgentRefusesUnreadableSessionRecordWithoutHostFallback()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        var directLaunches = new List<string>();
        await File.WriteAllTextAsync(_sessionStatePath!, "{ not json");

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => RunAgentWithDirectLaunchProbeAsync(runtime, directLaunches));

        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
        Assert.Contains("clawctl setup", failure.Message, StringComparison.Ordinal);
        Assert.Empty(directLaunches);
    }

    [Fact]
    public async Task AgentFallsBackToHostWhenSessionRuntimeIsUnavailable()
    {
        SessionRuntime runtime = await SetUpSessionAsync();
        _lastSessionBackend!.StartFailure = new MxcException(
            MxcErrorCode.RuntimeUnavailable,
            "The MXC runtime is not available.");
        bool directLaunchAttempted = false;
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        string archivePath = Path.Combine(_testDirectory, "node-v24.15.0-win-x64.zip");

        int exitCode = await Program.RunAgentAsync(
            new HostOptions(applicationDirectory, archivePath, ["--version"]),
            _ => { },
            _ => Task.FromResult(new NodeRuntime(
                "node.exe",
                new Version(24, 15, 0),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)),
            (_, _, _, _, _, _) =>
            {
                directLaunchAttempted = true;
                return Task.FromResult(17);
            },
            _ => runtime);

        Assert.True(directLaunchAttempted);
        Assert.Equal(17, exitCode);
    }

    [Fact]
    public async Task TeardownRequiresForceBeforeRemovingTheSession()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        using var error = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []),
            ["teardown"],
            _ => { },
            TextWriter.Null,
            error,
            createSessionRuntime: () => runtime);

        Assert.Equal(1, exitCode);
        Assert.Contains("--force", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            _lastSessionBackend!.Calls,
            call => call.StartsWith("deprovision:", StringComparison.Ordinal));
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
            ["teardown", "--force"],
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
        backend.ExecuteBehavior = _ =>
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
            return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
        };
        _lastSessionBackend = backend;
        var paths = HostPaths.ForRoot(stateRoot, "OpenClaw.Gateway_test");
        _sessionStatePath = paths.SessionStatePath;
        return SessionRuntime.Create(
            paths,
            () => throw new InvalidOperationException("The test supplies its backend."),
            baseDirectory,
            _ => { },
            backend);
    }

    private async Task<SessionRuntime> SetUpSessionAsync()
    {
        SessionRuntime runtime = CreateSessionRuntime();
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        string archivePath = Path.Combine(_testDirectory, "node-v24.15.0-win-x64.zip");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');").ConfigureAwait(false);
        await File.WriteAllTextAsync(archivePath, "fixture").ConfigureAwait(false);
        int exitCode = await Program.RunControlAsync(
            new HostOptions(applicationDirectory, archivePath, []),
            ["setup"],
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            _ => Task.FromResult(new NodeRuntime(
                "node.exe",
                new Version(24, 15, 0),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)),
            () => runtime).ConfigureAwait(false);

        Assert.Equal(0, exitCode);
        return runtime;
    }

    private static string SandboxIdFor(string applicationId) =>
        $"iso:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{{\"appId\":\"{applicationId}\"}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    private static MxcReadinessReport UnavailableReadiness() =>
        new(
            RuntimeDirectory: null,
            Provenance: null,
            RuntimeUnavailableReason: "The runtime is unavailable.",
            HostSupport: MxcHostSupport.Supported,
            HostBuild: null,
            SupportEvidence: MxcSupportEvidence.BackendProbe,
            BackendProbe: new MxcBackendProbe(false, "base-container", []),
            BackendProbeFailureReason: null);

    private static Task<int> RunAgentWithDirectLaunchProbeAsync(
        SessionRuntime runtime,
        List<string> directLaunches)
    {
        ArgumentNullException.ThrowIfNull(directLaunches);

        return Program.RunAgentAsync(
            new HostOptions(null, null, ["--version"]),
            _ => { },
            _ => throw new InvalidOperationException("Host Node must not be resolved."),
            (_, _, _, _, _, _) =>
            {
                directLaunches.Add("direct");
                return Task.FromResult(0);
            },
            _ => runtime);
    }
}
