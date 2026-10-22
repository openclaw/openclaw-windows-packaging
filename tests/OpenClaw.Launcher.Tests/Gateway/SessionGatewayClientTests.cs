using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class SessionGatewayClientTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private readonly FakeMxcSessionClient _backend = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Pending intent remains serializable for explicit teardown recovery; the
    // guest inspector returns an error rather than treating it as absence.
    [Fact]
    public void PendingInspectionRequestIsAcceptedWithoutAProcessIdentity()
    {
        SessionInspectRequest request = SessionInspectProtocol.ReadRequest(
            SessionInspectProtocol.SerializeRequest(new SessionInspectRequest
            {
                RequestId = "request",
                LaunchPending = true
            }));

        Assert.True(request.LaunchPending);
        Assert.Equal(0, request.ProcessId);
    }

    [Fact]
    public async Task StartUsesTheSharedWorkspaceRatherThanTheHostWorkingDirectory()
    {
        string workspace = Path.Combine(_root, "shared");
        string helperPath = Path.Combine(_root, "package", "openclaw-session-host.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, string.Empty);

        string stagedHelperPath = SessionHelperStager.ResolveStagedPath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedHelperPath)!);
        File.WriteAllText(stagedHelperPath, string.Empty);

        SessionLaunchRequest? delivered = null;
        _backend.ExecuteBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(workspace, "gateway-*.json")
                .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            delivered = SessionLaunchProtocol.ReadRequest(File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = delivered.RequestId,
                    Launched = true,
                    ProcessId = 1234,
                    ProcessStartTimeUtc = DateTimeOffset.UnixEpoch,
                }));
            return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
        };

        await new SessionGatewayClient(_backend, _ => { }).StartAsync(
            new SessionRecord
            {
                SandboxId = "iso:sandbox1",
                WorkspacePath = workspace,
                Generation = "test-generation",
            },
            new GatewayStartRequest(
                helperPath,
                @"C:\agent-node\node.exe",
                @"C:\Package\app",
                Port: null),
            CancellationToken.None);

        Assert.NotNull(delivered);
        Assert.Equal(workspace, delivered.WorkingDirectory);
    }

    // A failing helper writes its reason into the result but no request id.
    // Reporting only the mismatch dropped that reason from both the gateway
    // status detail and the log.
    [Fact]
    public async Task AFailedGuestInspectionKeepsTheGuestsOwnReason()
    {
        string workspace = Path.Combine(_root, "shared");
        string helperPath = Path.Combine(_root, "package", "openclaw-session-host.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, string.Empty);
        string stagedHelperPath = SessionHelperStager.ResolveStagedPath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedHelperPath)!);
        File.WriteAllText(stagedHelperPath, string.Empty);
        _backend.ExecuteBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(workspace, "inspect-*.json")
                .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionInspectProtocol.SerializeResult(new SessionInspectResult
                {
                    Error = "Access to the gateway status file is denied."
                }));
            return Task.FromResult(new MxcExecutionResult(
                SessionLaunchProtocol.HelperFailureExitCode,
                string.Empty,
                string.Empty));
        };
        List<string> log = [];

        SessionInspectResult result = await new SessionGatewayClient(_backend, log.Add)
            .InspectAsync(
                new SessionRecord
                {
                    SandboxId = "iso:sandbox1",
                    WorkspacePath = workspace,
                    Generation = "test-generation",
                },
                new GatewayRecord { SandboxId = "iso:sandbox1", ProcessId = 1234 },
                helperPath,
                CancellationToken.None);

        Assert.Contains(
            "The guest reported: Access to the gateway status file is denied.",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            log,
            line => line.StartsWith("Gateway inspection failed: ", StringComparison.Ordinal) &&
                line.Contains("Access to the gateway status file is denied.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeStartUsesSetupRecordedAgentNodeWithoutHostNodeResolution()
    {
        (SessionLaunchRequest delivered, string agentNodePath, _) =
            await StartGatewayAsync(nativeRootPath: null);

        Assert.Equal(agentNodePath, delivered.Executable);
        Assert.Equal(Path.GetDirectoryName(agentNodePath), delivered.PathPrefix);
        Assert.Equal(
            "enabled",
            delivered.Environment![OpenClawRuntimeEnvironment.GatewayIsolationVariable]);
    }

    // The detached gateway launch is a second guest boundary. The preload must
    // be a Node runtime argument so the gateway's reconstructed agent CLI keeps
    // it, and the staged root must be held for the gateway's lifetime.
    [Fact]
    public async Task ADetachedGatewayLaunchCarriesTheNativeRedirectOnNodeArguments()
    {
        string nativeRootPath = Path.Combine(_root, "agent-native", "content");
        Directory.CreateDirectory(nativeRootPath);

        (SessionLaunchRequest delivered, _, _) = await StartGatewayAsync(nativeRootPath);

        Assert.Equal(nativeRootPath, delivered.NativeRootPath);
        Assert.Equal("--import", delivered.Arguments![0]);
        Assert.Contains(
            OpenClawRuntimeEnvironment.NativeRedirectFileName,
            delivered.Arguments[1],
            StringComparison.Ordinal);
        Assert.EndsWith("openclaw.mjs", delivered.Arguments[2], StringComparison.Ordinal);
        Assert.Equal(["gateway", "run"], delivered.Arguments.Skip(3));
        Assert.Null(delivered.NodeOptionsSuffix);
        Assert.False(delivered.Environment!.ContainsKey("NODE_OPTIONS"));
    }

    // A session without staged natives must not carry a redirect it cannot use.
    [Fact]
    public async Task AGatewayLaunchWithoutStagedNativesNamesNoRedirect()
    {
        (SessionLaunchRequest delivered, _, _) = await StartGatewayAsync(nativeRootPath: null);

        Assert.Null(delivered.NativeRootPath);
        Assert.Null(delivered.NodeOptionsSuffix);
        Assert.EndsWith("openclaw.mjs", delivered.Arguments![0], StringComparison.Ordinal);
    }

    private async Task<(SessionLaunchRequest Delivered, string AgentNodePath, SessionRuntime Session)>
        StartGatewayAsync(string? nativeRootPath)
    {
        string applicationDirectory = Path.Combine(_root, "app");
        string runtimeDirectory = Path.Combine(_root, "runtime");
        string archivePath = Path.Combine(runtimeDirectory, "node-v24.20.0-win-x64.zip");
        string agentNodePath = Path.Combine(_root, "agent-node", "node.exe");
        Directory.CreateDirectory(applicationDirectory);
        Directory.CreateDirectory(runtimeDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(archivePath, string.Empty).ConfigureAwait(false);

        HostPaths paths = HostPaths.ForRoot(_root, "OpenClaw.Gateway_abc123");
        _backend.Metadata = new MxcProvisionMetadata(
            "agent_1",
            "S-1-5-21-0-0-0-1001",
            Path.Combine(_root, "shared"));
        SessionRuntime session = SessionRuntime.Create(
            paths,
            () => throw new InvalidOperationException("Host Node/runtime resolution is not expected."),
            _root,
            _ => { },
            _backend);
        SessionRecord record = await session.Coordinator.EnsureStartedAsync(CancellationToken.None)
            .ConfigureAwait(false);
        session.CompleteSetup(
            record,
            new SessionRuntimeInstallResult
            {
                ExecutablePath = agentNodePath,
                Version = "24.20.0",
                ArchiveName = Path.GetFileName(archivePath),
                NativeRootPath = nativeRootPath,
            },
            startupEnabled: true);
        Directory.CreateDirectory(Path.GetDirectoryName(session.HelperPath)!);
        await File.WriteAllTextAsync(session.HelperPath, string.Empty).ConfigureAwait(false);
        string stagedHelperPath = SessionHelperStager.ResolveStagedPath(record.WorkspacePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedHelperPath)!);
        await File.WriteAllTextAsync(stagedHelperPath, string.Empty).ConfigureAwait(false);

        SessionLaunchRequest? delivered = null;
        _backend.ExecuteBehavior = _ =>
        {
            string? inspectPath = Directory.GetFiles(record.WorkspacePath!, "inspect-*.json")
                .SingleOrDefault(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            if (inspectPath is not null)
            {
                SessionInspectRequest inspection = SessionInspectProtocol.ReadRequest(
                    File.ReadAllText(inspectPath));
                File.WriteAllText(
                    SessionLaunchProtocol.ResultPathFor(inspectPath),
                    SessionInspectProtocol.SerializeResult(new SessionInspectResult
                    {
                        RequestId = inspection.RequestId,
                        ProcessFound = true,
                        StartTimeMatches = true,
                        PortListening = true,
                        ListenerOwned = true,
                    }));
                return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
            }

            string requestPath = Directory.GetFiles(record.WorkspacePath!, "gateway-*.json")
                .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            delivered = SessionLaunchProtocol.ReadRequest(File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = delivered.RequestId,
                    Launched = true,
                    ProcessId = 1234,
                    ProcessStartTimeUtc = DateTimeOffset.UnixEpoch,
                }));
            return Task.FromResult(new MxcExecutionResult(0, string.Empty, string.Empty));
        };

        GatewayRuntime runtime = GatewayRuntime.Create(
            new HostOptions(applicationDirectory, archivePath, []),
            paths,
            session,
            _ => { });
        await runtime.Controller.StartAsync(runtime.HelperPath, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.NotNull(delivered);
        return (delivered, agentNodePath, session);
    }
}
