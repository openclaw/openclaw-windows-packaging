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
}
