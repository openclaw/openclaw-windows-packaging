using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcReadinessTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void ProbeReportsAMissingRuntimeWithoutFailing()
    {
        MxcReadinessReport report = MxcReadiness.Probe(
            _testDirectory,
            NoEnvironment,
            () => new MxcHostBuild(27000, 1));

        Assert.False(report.RuntimeAvailable);
        Assert.NotNull(report.RuntimeUnavailableReason);
        Assert.Null(report.RuntimeDirectory);
        Assert.Equal(MxcHostSupport.Supported, report.HostSupport);
    }

    [Fact]
    public void ProbeReportsAStagedRuntimeAndItsProvenance()
    {
        string runtimeDirectory = Path.Combine(_testDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.ExecutorFileName),
            string.Empty);
        File.WriteAllText(
            Path.Combine(
                runtimeDirectory,
                MxcRuntimeLocator.PackageLifecycleFileName),
            string.Empty);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.ProvenanceFileName),
            """
            {"package":"@microsoft/mxc-sdk","version":"0.8.0","architecture":"x64"}
            """);

        MxcReadinessReport report = MxcReadiness.Probe(
            _testDirectory,
            name => name == MxcRuntimeLocator.RuntimeDirectoryVariable
                ? runtimeDirectory
                : null,
            () => MxcReadiness.MinimumHostBuild);

        Assert.True(report.RuntimeAvailable);
        Assert.Equal(runtimeDirectory, report.RuntimeDirectory);
        Assert.Equal("0.8.0", report.Provenance?.Version);
    }

    [Theory]
    [InlineData(26339, 99999, MxcHostSupport.Unsupported)]
    [InlineData(26340, 9211, MxcHostSupport.Unsupported)]
    [InlineData(26340, 9212, MxcHostSupport.Supported)]
    [InlineData(26340, 9300, MxcHostSupport.Supported)]
    [InlineData(26341, 0, MxcHostSupport.Supported)]
    public void HostSupportComparesTheUpdateBuildRevisionNotJustTheBuild(
        int build,
        int updateBuildRevision,
        MxcHostSupport expected)
    {
        MxcReadinessReport report = MxcReadiness.Probe(
            _testDirectory,
            NoEnvironment,
            () => new MxcHostBuild(build, updateBuildRevision));

        Assert.Equal(expected, report.HostSupport);
    }

    [Fact]
    public void AnUndeterminableHostBuildIsReportedAsUnknownNotUnsupported()
    {
        // Reporting unsupported would tell a capable machine's user that
        // isolated sessions can never work there.
        MxcReadinessReport report = MxcReadiness.Probe(
            _testDirectory,
            NoEnvironment,
            () => null);

        Assert.Equal(MxcHostSupport.Unknown, report.HostSupport);
        Assert.Null(report.HostBuild);
    }

    [Fact]
    public void ProbeDoesNotCreateOrModifyAnything()
    {
        MxcReadiness.Probe(_testDirectory, NoEnvironment, () => null);

        Assert.Empty(Directory.GetFileSystemEntries(_testDirectory));
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
