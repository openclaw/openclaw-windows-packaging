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

    [Fact]
    public async Task RuntimeStartUsesSetupRecordedAgentNodeWithoutHostNodeResolution()
    {
        string applicationDirectory = Path.Combine(_root, "app");
        string runtimeDirectory = Path.Combine(_root, "runtime");
        string archivePath = Path.Combine(runtimeDirectory, "node-v24.20.0-win-x64.zip");
        string agentNodePath = Path.Combine(_root, "agent-node", "node.exe");
        Directory.CreateDirectory(applicationDirectory);
        Directory.CreateDirectory(runtimeDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        await File.WriteAllTextAsync(archivePath, string.Empty);

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
        SessionRecord record = await session.Coordinator.EnsureStartedAsync(CancellationToken.None);
        session.CompleteSetup(
            record,
            new SessionRuntimeInstallResult
            {
                ExecutablePath = agentNodePath,
                Version = "24.20.0",
                ArchiveName = Path.GetFileName(archivePath),
            },
            startupEnabled: true);
        Directory.CreateDirectory(Path.GetDirectoryName(session.HelperPath)!);
        await File.WriteAllTextAsync(session.HelperPath, string.Empty);
        string stagedHelperPath = SessionHelperStager.ResolveStagedPath(record.WorkspacePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedHelperPath)!);
        await File.WriteAllTextAsync(stagedHelperPath, string.Empty);

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
        await runtime.Controller.StartAsync(runtime.HelperPath, CancellationToken.None);

        Assert.NotNull(delivered);
        Assert.Equal(agentNodePath, delivered.Executable);
        Assert.Equal(Path.GetDirectoryName(agentNodePath), delivered.PathPrefix);
    }
}
