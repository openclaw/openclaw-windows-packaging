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
    public async Task SetupReportsAnUnavailableIsolatedSessionWithoutResolvingHostNode()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        string entryPoint = Path.Combine(applicationDirectory, "openclaw.mjs");
        await File.WriteAllTextAsync(entryPoint, "console.log('fixture');");
        DateTime lastWriteTime = File.GetLastWriteTimeUtc(entryPoint);
        var options = new HostOptions(applicationDirectory, null, []);
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
            _ => Task.FromResult(nodeRuntime));

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(entryPoint));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(entryPoint));
        Assert.DoesNotContain(
            nodeRuntime.ExecutablePath,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            applicationDirectory,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "could not complete",
            output.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetupReportsMissingApplicationBeforeResolvingHostNode()
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

        Assert.False(nodeResolutionAttempted);
    }

    [Fact]
    public async Task AgentReportsMissingApplicationBeforeResolvingHostNode()
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

        Assert.False(nodeResolutionAttempted);
    }

    [Fact]
    public async Task AutomaticAgentLaunchUsesHostOnlyWhenReadinessReportsIsolationUnsupported()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        bool resolvedHostNode = false;
        bool launchedHost = false;

        int exitCode = await Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, []),
            _ => { },
            _ =>
            {
                resolvedHostNode = true;
                return Task.FromResult(new NodeRuntime(
                    "node.exe",
                    new Version(24, 0),
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
            },
            (_, _, _, _, _) =>
            {
                launchedHost = true;
                return Task.FromResult(17);
            },
            probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                "runtime",
                null,
                null,
                MxcHostSupport.Unsupported,
                null,
                MxcSupportEvidence.HostBuild)),
            getPackageFamilyName: () => "OpenClaw.Gateway_test",
            readEnvironmentVariable: _ => null);

        Assert.Equal(17, exitCode);
        Assert.True(resolvedHostNode);
        Assert.True(launchedHost);
    }

    [Fact]
    public async Task RequiredAgentLaunchFailsWhenReadinessReportsIsolationUnsupported()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        bool resolvedHostNode = false;

        await Assert.ThrowsAsync<SessionException>(() => Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, []),
            _ => { },
            _ =>
            {
                resolvedHostNode = true;
                return Task.FromResult(new NodeRuntime(
                    "node.exe",
                    new Version(24, 0),
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
            },
            (_, _, _, _, _) => Task.FromResult(0),
            probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                "runtime",
                null,
                null,
                MxcHostSupport.Unsupported,
                null,
                MxcSupportEvidence.HostBuild)),
            getPackageFamilyName: () => "OpenClaw.Gateway_test",
            readEnvironmentVariable: name =>
                name == SessionRoutingPolicy.ModeVariable ? "1" : null));

        Assert.False(resolvedHostNode);
    }

    [Fact]
    public async Task SelectedSessionFailureDoesNotResolveOrLaunchHostNode()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(Path.Combine(applicationDirectory, "openclaw.mjs"), string.Empty);
        bool resolvedHostNode = false;
        bool launchedHost = false;

        await Assert.ThrowsAsync<SessionException>(() => Program.RunAgentAsync(
            new HostOptions(applicationDirectory, null, []),
            _ => { },
            _ =>
            {
                resolvedHostNode = true;
                return Task.FromResult(new NodeRuntime(
                    "node.exe",
                    new Version(24, 0),
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
            },
            (_, _, _, _, _) =>
            {
                launchedHost = true;
                return Task.FromResult(0);
            },
            _ => throw new SessionException("setup failed"),
            _ => Task.FromResult(new MxcReadinessReport(
                "runtime",
                null,
                null,
                MxcHostSupport.Supported,
                null,
                MxcSupportEvidence.HostBuild)),
            () => "OpenClaw.Gateway_test",
            _ => null));

        Assert.False(resolvedHostNode);
        Assert.False(launchedHost);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
