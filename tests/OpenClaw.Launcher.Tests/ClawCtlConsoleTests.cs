using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlConsoleTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void WriteHelpListsOnlyThePublicCommands()
    {
        var output = new StringWriter();

        ClawCtlConsole.WriteHelp(output);

        string help = output.ToString();
        Assert.Contains("setup", help, StringComparison.Ordinal);
        Assert.DoesNotContain("prepare", help, StringComparison.Ordinal);
        Assert.DoesNotContain("verify", help, StringComparison.Ordinal);
        Assert.DoesNotContain("repair", help, StringComparison.Ordinal);
        Assert.Contains(
            NodeRuntimeResolver.SupportedVersions,
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            NodeRuntimeResolver.InstallCommand,
            help,
            StringComparison.Ordinal);
        Assert.DoesNotContain("update-package", help, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway-service", help, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteReadinessSummaryDescribesPackagedApplication()
    {
        var output = new StringWriter();
        string applicationDirectory = Path.Combine(_testDirectory, "app");

        ClawCtlConsole.WriteReadinessSummary(
            output,
            applicationDirectory);

        string summary = output.ToString();
        Assert.Contains("package is ready", summary, StringComparison.Ordinal);
        Assert.Contains(applicationDirectory, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MxcReadinessSummaryNamesTheStagedRuntimeAndHostBuild()
    {
        var output = new StringWriter();

        ClawCtlConsole.WriteMxcReadinessSummary(
            output,
            new MxcReadinessReport(
                @"C:\package\mxc\x64",
                new MxcRuntimeProvenance("@microsoft/mxc-sdk", "0.8.0", "x64"),
                RuntimeUnavailableReason: null,
                MxcHostSupport.Supported,
                new MxcHostBuild(26340, 9300)));

        string summary = output.ToString();
        Assert.Contains("@microsoft/mxc-sdk 0.8.0", summary, StringComparison.Ordinal);
        Assert.Contains(@"C:\package\mxc\x64", summary, StringComparison.Ordinal);
        Assert.Contains("26340.9300", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MxcReadinessSummaryExplainsAnUnsupportedHostBuild()
    {
        var output = new StringWriter();

        ClawCtlConsole.WriteMxcReadinessSummary(
            output,
            new MxcReadinessReport(
                @"C:\package\mxc\x64",
                new MxcRuntimeProvenance("@microsoft/mxc-sdk", "0.8.0", "x64"),
                RuntimeUnavailableReason: null,
                MxcHostSupport.Unsupported,
                new MxcHostBuild(26100, 3194)));

        string summary = output.ToString();
        Assert.Contains("26100.3194", summary, StringComparison.Ordinal);
        Assert.Contains(
            MxcReadiness.MinimumHostBuild.ToString(),
            summary,
            StringComparison.Ordinal);
        Assert.Contains(
            "Isolated sessions are unavailable",
            summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MxcReadinessSummaryDistinguishesAnUnknownHostBuild()
    {
        var output = new StringWriter();

        ClawCtlConsole.WriteMxcReadinessSummary(
            output,
            new MxcReadinessReport(
                RuntimeDirectory: null,
                Provenance: null,
                "wxc-exec.exe is missing.",
                MxcHostSupport.Unknown,
                HostBuild: null));

        string summary = output.ToString();
        Assert.Contains("could not be determined", summary, StringComparison.Ordinal);
        Assert.Contains("wxc-exec.exe is missing.", summary, StringComparison.Ordinal);
        Assert.Contains(
            "Isolated sessions are unavailable",
            summary,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
