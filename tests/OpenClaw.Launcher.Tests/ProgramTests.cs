using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

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
                    "test routes directly")),
            (_, _, _, _, _) =>
                throw new InvalidOperationException(
                    "Direct routing must not enter a session."));

        Assert.True(nodeResolutionAttempted);
        Assert.True(launchAttempted);
        Assert.Equal(23, exitCode);
    }

    [Fact]
    public async Task AgentLaunchRoutesThroughTheSessionWhenRoutingSelectsIt()
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
        bool sessionAttempted = false;

        int exitCode = await Program.RunAgentAsync(
            options,
            _ => { },
            _ => Task.FromResult(nodeRuntime),
            (_, _, _, _, _) =>
                throw new InvalidOperationException(
                    "Session routing must not launch on the host."),
            _ => Task.FromResult(
                new SessionRoutingDecision(
                    SessionRouting.Session,
                    "test routes through a session")),
            (resolvedNode, appDirectory, forwardedArguments, _, _) =>
            {
            sessionAttempted = true;
            Assert.Equal(nodeRuntime.ExecutablePath, resolvedNode.ExecutablePath);
            Assert.Equal(applicationDirectory, appDirectory);
            Assert.Equal(arguments, forwardedArguments);
            return Task.FromResult(31);
            });

        Assert.True(sessionAttempted);
        Assert.Equal(31, exitCode);
    }

    [Fact]
    public async Task AgentLaunchSurfacesSessionFailuresInsteadOfFallingBack()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        var options = new HostOptions(applicationDirectory, ["--help"]);
        var nodeRuntime = new NodeRuntime(
            Path.Combine(_testDirectory, "node.exe"),
            new Version(24, 15, 0),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

        SessionException failure =
            await Assert.ThrowsAsync<SessionException>(
                () => Program.RunAgentAsync(
                    options,
                    _ => { },
                    _ => Task.FromResult(nodeRuntime),
                    (_, _, _, _, _) =>
                        throw new InvalidOperationException(
                            "A failed session must never fall back to the host."),
                    _ => Task.FromResult(
                        new SessionRoutingDecision(
                            SessionRouting.Session,
                            "test routes through a session")),
                    (_, _, _, _, _) =>
                        throw new SessionException("the backend refused")));

        Assert.Equal("the backend refused", failure.Message);
    }

    [Fact]
    public async Task SetupChecksNodeAndPackagedApplicationWithoutMutation()
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
        var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            options,
            ["setup"],
            _ => { },
            _ => { },
            output,
            _ => Task.FromResult(nodeRuntime),
            _ => Task.FromResult(new MxcReadinessReport(
                @"C:\package\mxc\x64",
                new MxcRuntimeProvenance("@microsoft/mxc-sdk", "0.8.0", "x64"),
                RuntimeUnavailableReason: null,
                MxcHostSupport.Supported,
                MxcReadiness.MinimumHostBuild,
                MxcSupportEvidence.BackendProbe,
                new MxcBackendProbe(true, "base-container", []))));

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
        Assert.Contains(
            "0.8.0",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupReportsMissingSessionSupportWithoutFailingOverall()
    {
        // OpenClaw still runs without isolated sessions, so setup keeps its
        // successful exit code and simply says the feature is unavailable.
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(applicationDirectory, []),
            ["setup"],
            _ => { },
            _ => { },
            output,
            _ => Task.FromResult(
                new NodeRuntime(
                    Path.Combine(_testDirectory, "node.exe"),
                    new Version(24, 15, 0),
                    System.Runtime.InteropServices.RuntimeInformation
                        .ProcessArchitecture)),
            _ => Task.FromResult(new MxcReadinessReport(
                RuntimeDirectory: null,
                Provenance: null,
                "wxc-exec.exe is missing.",
                MxcHostSupport.Unsupported,
                new MxcHostBuild(26100, 1),
                MxcSupportEvidence.HostBuild)));

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "wxc-exec.exe is missing.",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "Isolated sessions are unavailable",
            output.ToString(),
            StringComparison.Ordinal);
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
            _ => { },
            new StringWriter(),
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
            }));

        Assert.True(nodeResolutionAttempted);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
