using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcReadinessTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    /// <summary>
    /// Stands in for a host whose backend detector cannot run, which is the
    /// case whenever the runtime itself is missing.
    /// </summary>
    private static Task<MxcBackendProbe> BackendUnreachable(
        MxcRuntimeLocation location,
        CancellationToken cancellationToken) =>
        Task.FromException<MxcBackendProbe>(
            new MxcException(
                MxcErrorCode.RuntimeUnavailable,
                "probe unavailable"));

    private static Func<MxcRuntimeLocation, CancellationToken, Task<MxcBackendProbe>>
        BackendReports(bool available, string? tier = "base-container") =>
        (_, _) => Task.FromResult(new MxcBackendProbe(available, tier, []));

    private MxcRuntimeLocation LocateInTestDirectory() =>
        MxcRuntimeLocator.Locate(_testDirectory, _ => { });

    private string StageRuntime()
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, MxcRuntimeLocator.NativeLibraryFileName),
            string.Empty);
        File.WriteAllText(
            Path.Combine(_testDirectory, MxcRuntimeLocator.PackageLifecycleFileName),
            string.Empty);
        return Path.GetFullPath(_testDirectory);
    }

    [Fact]
    public async Task ProbeReportsAMissingRuntimeWithoutFailing()
    {
        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            LocateInTestDirectory,
            () => new MxcHostBuild(27000, 1),
            BackendUnreachable,
            CancellationToken.None);

        Assert.False(report.RuntimeAvailable);
        Assert.NotNull(report.RuntimeUnavailableReason);
        Assert.Null(report.RuntimeDirectory);

        // Without the runtime the backend cannot be asked, so support falls
        // back to the documented build minimum and says so.
        Assert.Equal(MxcHostSupport.Supported, report.HostSupport);
        Assert.Equal(MxcSupportEvidence.HostBuild, report.SupportEvidence);
    }

    [Fact]
    public async Task ProbeReportsAStagedRuntime()
    {
        string runtimeDirectory = StageRuntime();

        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            LocateInTestDirectory,
            () => MxcReadiness.MinimumHostBuild,
            BackendReports(available: true),
            CancellationToken.None);

        Assert.True(report.RuntimeAvailable);
        Assert.Equal(runtimeDirectory, report.RuntimeDirectory);
    }

    [Fact]
    public async Task BackendProbeOverridesAnOtherwiseSupportedBuild()
    {
        // The decisive case: a new enough build whose backend is nonetheless
        // unusable. Trusting the build number alone would promise a capability
        // this host does not have.
        string runtimeDirectory = StageRuntime();

        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            LocateInTestDirectory,
            () => new MxcHostBuild(27000, 1),
            BackendReports(available: false),
            CancellationToken.None);

        Assert.Equal(MxcHostSupport.Unsupported, report.HostSupport);
        Assert.Equal(MxcSupportEvidence.BackendProbe, report.SupportEvidence);
    }

    [Fact]
    public async Task BackendProbeOverridesAnOtherwiseUnsupportedBuild()
    {
        string runtimeDirectory = StageRuntime();

        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            LocateInTestDirectory,
            () => new MxcHostBuild(19045, 1),
            BackendReports(available: true),
            CancellationToken.None);

        Assert.Equal(MxcHostSupport.Supported, report.HostSupport);
        Assert.Equal(MxcSupportEvidence.BackendProbe, report.SupportEvidence);
    }

    [Fact]
    public async Task AFailedBackendProbeDegradesToTheBuildCheckAndIsReported()
    {
        string runtimeDirectory = StageRuntime();

        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            LocateInTestDirectory,
            () => new MxcHostBuild(27000, 1),
            (_, _) => throw new InvalidOperationException("native probe crashed"),
            CancellationToken.None);

        Assert.Equal(MxcHostSupport.Supported, report.HostSupport);
        Assert.Equal(MxcSupportEvidence.HostBuild, report.SupportEvidence);
        Assert.Contains("native probe crashed", report.BackendProbeFailureReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(26339, 99999, nameof(MxcHostSupport.Unsupported))]
    [InlineData(26340, 9211, nameof(MxcHostSupport.Unsupported))]
    [InlineData(26340, 9212, nameof(MxcHostSupport.Supported))]
    [InlineData(26340, 9300, nameof(MxcHostSupport.Supported))]
    [InlineData(26341, 0, nameof(MxcHostSupport.Supported))]
    public async Task HostSupportComparesTheUpdateBuildRevisionNotJustTheBuild(
        int build,
        int updateBuildRevision,
        string expectedName)
    {
        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            LocateInTestDirectory,
            () => new MxcHostBuild(build, updateBuildRevision),
            BackendUnreachable,
            CancellationToken.None);

        Assert.Equal(Enum.Parse<MxcHostSupport>(expectedName), report.HostSupport);
    }

    [Fact]
    public async Task AnUndeterminableHostBuildIsReportedAsUnknownNotUnsupported()
    {
        // Reporting unsupported would tell a capable machine's user that
        // isolated sessions can never work there.
        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            LocateInTestDirectory,
            () => null,
            BackendUnreachable,
            CancellationToken.None);

        Assert.Equal(MxcHostSupport.Unknown, report.HostSupport);
        Assert.Null(report.HostBuild);
        Assert.Equal(MxcSupportEvidence.None, report.SupportEvidence);
    }

    [Fact]
    public async Task ProbeDoesNotCreateOrModifyAnything()
    {
        await MxcReadiness.ProbeAsync(
            LocateInTestDirectory,
            () => null,
            BackendUnreachable,
            CancellationToken.None);

        Assert.Empty(Directory.GetFileSystemEntries(_testDirectory));
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
