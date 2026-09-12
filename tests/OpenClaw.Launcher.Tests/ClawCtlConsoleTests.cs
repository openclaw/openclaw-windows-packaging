using OpenClaw.Launcher.Mxc;
namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlConsoleTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void WriteReadinessSummaryDescribesPackagedApplication()
    {
        using var output = new StringWriter();
        string applicationDirectory = Path.Combine(_testDirectory, "app");

        ClawCtlConsole.WriteReadinessSummary(
            output,
            applicationDirectory);

        string summary = output.ToString();
        Assert.Contains("package is ready", summary, StringComparison.Ordinal);
        Assert.Contains(applicationDirectory, summary, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
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
                new MxcHostBuild(26340, 9300),
                MxcSupportEvidence.HostBuild));

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
                new MxcHostBuild(26100, 3194),
                MxcSupportEvidence.HostBuild));

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
    public void MxcReadinessSummaryPrefersTheMeasuredBackendVerdict()
    {
        // A build number above the documented minimum must not be reported as
        // support when the backend itself says the feature is unusable here.
        var output = new StringWriter();

        ClawCtlConsole.WriteMxcReadinessSummary(
            output,
            new MxcReadinessReport(
                @"C:\package\mxc\x64",
                new MxcRuntimeProvenance("@microsoft/mxc-sdk", "0.8.0", "x64"),
                RuntimeUnavailableReason: null,
                MxcHostSupport.Unsupported,
                new MxcHostBuild(27000, 1),
                MxcSupportEvidence.BackendProbe,
                new MxcBackendProbe(false, "none", ["host preparation required"])));

        string summary = output.ToString();
        Assert.Contains("not available on this host", summary, StringComparison.Ordinal);
        Assert.Contains("host preparation required", summary, StringComparison.Ordinal);
        Assert.Contains(
            "Isolated sessions are unavailable",
            summary,
            StringComparison.Ordinal);

        // The build number predicted support, so quoting it here would
        // contradict the measured verdict.
        Assert.DoesNotContain("27000.1", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MxcReadinessSummaryReportsAFailedBackendProbeAlongsideTheFallback()
    {
        var output = new StringWriter();

        ClawCtlConsole.WriteMxcReadinessSummary(
            output,
            new MxcReadinessReport(
                @"C:\package\mxc\x64",
                new MxcRuntimeProvenance("@microsoft/mxc-sdk", "0.8.0", "x64"),
                RuntimeUnavailableReason: null,
                MxcHostSupport.Supported,
                new MxcHostBuild(27000, 1),
                MxcSupportEvidence.HostBuild,
                BackendProbe: null,
                BackendProbeFailureReason: "the probe exited with code 1"));

        string summary = output.ToString();
        Assert.Contains("could not be queried", summary, StringComparison.Ordinal);
        Assert.Contains(
            "the probe exited with code 1",
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
                HostBuild: null,
                MxcSupportEvidence.None));

        string summary = output.ToString();
        Assert.Contains("could not be determined", summary, StringComparison.Ordinal);
        Assert.Contains("wxc-exec.exe is missing.", summary, StringComparison.Ordinal);
        Assert.Contains(
            "Isolated sessions are unavailable",
            summary,
            StringComparison.Ordinal);
    }
}
