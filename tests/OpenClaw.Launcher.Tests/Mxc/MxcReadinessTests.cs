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

    private string StageRuntime()
    {
        string runtimeDirectory = Path.Combine(_testDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.ProvenanceFileName),
            """
            {"package":"Microsoft.Mxc.Sdk","version":"1.0.1","architecture":"x64"}
            """);
        return runtimeDirectory;
    }

    private static Func<MxcRuntimeLocation> RuntimeAt(string runtimeDirectory) =>
        () => MxcRuntimeLocator.Locate(runtimeDirectory);

    [Fact]
    public async Task ProbeReportsAMissingRuntimeWithoutFailing()
    {
        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            RuntimeAt(_testDirectory),
            () => new MxcHostBuild(27000, 1),
            BackendUnreachable,
            CancellationToken.None);

        Assert.False(report.RuntimeAvailable);
        Assert.NotNull(report.RuntimeUnavailableReason);
        Assert.Equal(_testDirectory, report.RuntimeDirectory);

        // SDK API failure leaves only host-build evidence.
        Assert.Equal(MxcHostSupport.Supported, report.HostSupport);
        Assert.Equal(MxcSupportEvidence.HostBuild, report.SupportEvidence);
    }

    [Fact]
    public async Task ProbeReportsAStagedRuntimeAndItsProvenance()
    {
        string runtimeDirectory = StageRuntime();

        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            RuntimeAt(runtimeDirectory),
            () => MxcReadiness.MinimumHostBuild,
            BackendReports(available: true),
            CancellationToken.None);

        Assert.True(report.RuntimeAvailable);
        Assert.Equal(runtimeDirectory, report.RuntimeDirectory);
        Assert.Equal("1.0.1", report.Provenance?.Version);
    }

    [Fact]
    public async Task BackendProbeOverridesAnOtherwiseSupportedBuild()
    {
        // The decisive case: a new enough build whose backend is nonetheless
        // unusable. Trusting the build number alone would promise a capability
        // this host does not have.
        string runtimeDirectory = StageRuntime();

        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            RuntimeAt(runtimeDirectory),
            () => new MxcHostBuild(27000, 1),
            BackendReports(available: false),
            CancellationToken.None);

        Assert.Equal(MxcHostSupport.Unsupported, report.HostSupport);
        Assert.Equal(MxcSupportEvidence.BackendProbe, report.SupportEvidence);
    }

    [Fact]
    public async Task UnavailableProbeFromANonInteractiveLogonIsRefusedForTheLogon()
    {
        string runtimeDirectory = StageRuntime();

        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            RuntimeAt(runtimeDirectory),
            () => new MxcHostBuild(29683, 1000),
            BackendReports(available: false),
            CancellationToken.None,
            () => false);

        Assert.Contains("caller logon non-interactive", report.Describe(), StringComparison.Ordinal);
        OpenClaw.Launcher.Session.InteractiveLogonRequiredException failure =
            Assert.Throws<OpenClaw.Launcher.Session.InteractiveLogonRequiredException>(
                () => OpenClaw.Launcher.Session.SessionSupportPolicy.EnsureSupported(
                    "OpenClaw.Gateway_abc123",
                    report));
        Assert.Contains("interactive sign-in", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendProbeOverridesAnOtherwiseUnsupportedBuild()
    {
        string runtimeDirectory = StageRuntime();

        MxcReadinessReport report = await MxcReadiness.ProbeAsync(
            RuntimeAt(runtimeDirectory),
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
            RuntimeAt(runtimeDirectory),
            () => new MxcHostBuild(27000, 1),
            (_, _) => throw new InvalidOperationException("executor crashed"),
            CancellationToken.None);

        Assert.Equal(MxcHostSupport.Supported, report.HostSupport);
        Assert.Equal(MxcSupportEvidence.HostBuild, report.SupportEvidence);
        Assert.Contains("executor crashed", report.BackendProbeFailureReason, StringComparison.Ordinal);
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
            RuntimeAt(_testDirectory),
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
            RuntimeAt(_testDirectory),
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
            RuntimeAt(_testDirectory),
            () => null,
            BackendUnreachable,
            CancellationToken.None);

        Assert.Empty(Directory.GetFileSystemEntries(_testDirectory));
    }

    // A refusal with no probe result looks identical to a refusal the backend
    // measured; the description is what tells them apart in a bundle.
    [Fact]
    public void AFailedProbeIsDescribedWithTheBuildEvidenceUsedInstead()
    {
        var report = new MxcReadinessReport(
            null,
            null,
            "The MXC runtime is not available.",
            MxcHostSupport.Unsupported,
            new MxcHostBuild(26100, 4000),
            MxcSupportEvidence.HostBuild,
            BackendProbeFailureReason: "The MXC host capability probe failed (exit code 1).");

        Assert.Equal(
            "Unsupported from HostBuild evidence; host build 26100.4000; " +
            "runtime unavailable (The MXC runtime is not available.); " +
            "backend probe failed (The MXC host capability probe failed (exit code 1).)",
            report.Describe());
    }

    [Fact]
    public void AProbeWithoutWarningsSaysSo()
    {
        var report = new MxcReadinessReport(
            @"C:\package\mxc\x64",
            new MxcRuntimeProvenance("Microsoft.Mxc.Sdk", "1.0.0", "x64"),
            null,
            MxcHostSupport.Supported,
            null,
            MxcSupportEvidence.BackendProbe,
            new MxcBackendProbe(true, null, []));

        Assert.Equal(
            "Supported from BackendProbe evidence; host build unknown; " +
            "runtime Microsoft.Mxc.Sdk 1.0.0 x64; backend probe isolation sessions " +
            "available, tier unreported, warnings none",
            report.Describe());
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
