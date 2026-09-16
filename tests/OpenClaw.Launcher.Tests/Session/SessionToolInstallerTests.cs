using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionToolInstallerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GuestInstallerCreatesTheCommandShimInItsWorkspace()
    {
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        string requestPath = Path.Combine(workspace, "tools.json");
        File.WriteAllText(
            requestPath,
            SessionRuntimeProtocol.SerializeToolInstallRequest(new SessionToolInstallRequest
            {
                RequestId = "tools1",
                WorkspacePath = workspace
            }));

        int exitCode = SessionToolInstaller.Run(
            requestPath,
            File.ReadAllText,
            File.WriteAllText);

        Assert.Equal(0, exitCode);
        SessionToolInstallResult result = SessionRuntimeProtocol.ReadToolInstallResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(requestPath)));
        Assert.Equal("tools1", result.RequestId);
        Assert.True(File.Exists(result.ShimPath));
        Assert.Equal(
            Path.Combine(workspace, ".openclaw-tools", "openclaw.cmd"),
            result.ShimPath);
    }
}
