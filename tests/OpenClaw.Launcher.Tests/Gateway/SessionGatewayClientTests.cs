using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;

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
    public async Task StartInvokesTheFullyQualifiedStagedHelper()
    {
        string packagedHelper = CreatePackagedHelper();
        string workspace = Path.Combine(_root, "workspace");
        string stagedHelper = SessionHelperStager.Stage(
            packagedHelper,
            workspace);
        SessionGatewayClient client = new(_backend, _ => { });

        await Assert.ThrowsAsync<SessionException>(
            () => client.StartAsync(
                Record(workspace),
                new GatewayStartRequest(
                    packagedHelper,
                    @"C:\Program Files\nodejs\node.exe",
                    @"C:\Program Files\WindowsApps\OpenClaw.Gateway\app",
                    workspace,
                    4517),
                CancellationToken.None));

        string commandLine = Assert.Single(_backend.ExecutedCommandLines);
        Assert.True(Path.IsPathFullyQualified(stagedHelper));
        Assert.Contains(stagedHelper, commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain(packagedHelper, commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartRefusesToDispatchBeforeSetupStagesTheHelper()
    {
        string packagedHelper = CreatePackagedHelper();
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        SessionGatewayClient client = new(_backend, _ => { });

        SessionException failure = await Assert.ThrowsAsync<SessionException>(
            () => client.StartAsync(
                Record(workspace),
                new GatewayStartRequest(
                    packagedHelper,
                    "node.exe",
                    "app",
                    workspace,
                    4517),
                CancellationToken.None));

        Assert.Contains("clawctl setup", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
    }

    private string CreatePackagedHelper()
    {
        string path = Path.Combine(
            _root,
            "package",
            "session-host",
            "x64",
            "openclaw-session-host.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "helper bytes");
        return path;
    }

    private static SessionRecord Record(string workspace) => new()
    {
        SandboxId = "iso:sandbox1",
        ApplicationId = "PFN:OpenClaw.Gateway_test",
        WorkspacePath = workspace
    };
}
