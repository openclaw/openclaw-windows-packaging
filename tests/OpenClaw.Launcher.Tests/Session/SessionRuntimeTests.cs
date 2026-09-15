using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionRuntimeTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void StatusDoesNotRequireTheMxcRuntimeToBeInstalled()
    {
        bool runtimeLocated = false;

        SessionRuntime host = SessionRuntime.Create(
            HostPaths.ForRoot(_root, "OpenClaw.Gateway_abc123"),
            () =>
            {
                runtimeLocated = true;
                throw new InvalidOperationException("no runtime here");
            },
            _root,
            _ => { });

        SessionStatus status = host.Coordinator.GetRecordedStatus();

        Assert.Equal(SessionAvailability.None, status.Availability);

        // A read-only status query must never be turned into a runtime
        // availability failure; the recorded state is what the user asked for.
        Assert.False(runtimeLocated);
    }

    [Fact]
    public void UnpackagedExecutionIsRefusedWithAnExplicitReason()
    {
        SessionException failure = Assert.Throws<SessionException>(
            () => SessionRuntime.Create(
                HostPaths.ForRoot(_root, packageFamilyName: null),
                () => throw new InvalidOperationException("not reached"),
                _root,
                _ => { }));

        Assert.Contains("installed package", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireSetupRefusesAnUnmarkedInstallationWithoutContactingMxc()
    {
        var backend = new FakeMxcSessionClient();
        SessionRuntime host = SessionRuntime.Create(
            HostPaths.ForRoot(_root, "OpenClaw.Gateway_abc123"),
            () => throw new InvalidOperationException("not reached"),
            _root,
            _ => { },
            backend);

        SessionException failure = Assert.Throws<SessionException>(
            host.RequireSetup);

        Assert.Contains("clawctl setup", failure.Message, StringComparison.Ordinal);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task CompletedSetupAuthorizesTheRecordedSessionAndAgentRuntime()
    {
        var backend = new FakeMxcSessionClient();
        SessionRuntime host = SessionRuntime.Create(
            HostPaths.ForRoot(_root, "OpenClaw.Gateway_abc123"),
            () => throw new InvalidOperationException("not reached"),
            _root,
            _ => { },
            backend);
        SessionRecord session = await host.Coordinator.EnsureStartedAsync(CancellationToken.None);

        host.CompleteSetup(
            session,
            new OpenClaw.SessionProtocol.SessionRuntimeInstallResult
            {
                ExecutablePath = @"C:\Users\agent_1\AppData\Local\OpenClawGatewayMSIX\agent-node\node.exe",
                Version = "24.20.0",
                ArchiveName = "node-v24.20.0-win-x64.zip"
            },
            startupEnabled: true);

        Assert.Equal(session.SandboxId, host.RequireSetup().SandboxId);
        Assert.Equal(
            @"C:\Users\agent_1\AppData\Local\OpenClawGatewayMSIX\agent-node\node.exe",
            host.RequireAgentNodePath(new Version(24, 20, 0)));
    }

    [Fact]
    public void TheGuestHelperIsResolvedPerArchitectureBesideTheMxcRuntime()
    {
        string helperPath = SessionRuntime.ResolveHelperPath(@"C:\package");

        Assert.StartsWith(
            Path.Combine(@"C:\package", SessionRuntime.HelperDirectoryName),
            helperPath,
            StringComparison.Ordinal);
        Assert.EndsWith(SessionRuntime.HelperFileName, helperPath, StringComparison.Ordinal);
        Assert.Contains(
            MxcRuntimeLocator.CurrentArchitectureName(),
            helperPath,
            StringComparison.Ordinal);
    }
}
