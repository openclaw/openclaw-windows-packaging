using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Covers the read-only native stage report behind <c>status --native</c>.
/// </summary>
public sealed class SessionNativeStageInspectorTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A link the verifier rejects must not be walked again for the report:
    /// a cycle back into the package would otherwise recurse until the helper
    /// fails, replacing the known mismatch with an unavailable report.
    /// </summary>
    [Fact]
    public void ARejectedLinkIsReportedWithoutBeingWalked()
    {
        string application = NativeStagePackageFixture.WriteApplication(
            Path.Combine(_root, "package"),
            new Dictionary<string, string>
            {
                ["node_modules/koffi/package.json"] = "{\"version\":\"2.0.0\"}",
                ["node_modules/koffi/koffi.node"] = "native"
            });
        string root = SessionNativeStager.Stage(application, Path.Combine(_root, "local"))!;
        string package = Path.Combine(root, "node_modules", "koffi");
        string link = Path.Combine(package, "loop");
        NativeStagePackageFixture.CreateJunction(link, package);
        try
        {
            SessionNativeStageReport report = SessionNativeStageInspector.Inspect(application, root);

            Assert.Equal("unsupported-layout", report.State);
            SessionNativeStagePackage koffi = Assert.Single(report.Packages);
            Assert.Equal("2.0.0", koffi.SourceVersion);
            Assert.Equal("unknown", koffi.ResolvesFrom);
            Assert.False(koffi.FilesMatch);
            Assert.Null(koffi.StagedDigest);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// A recorded stage that cannot be leased is not read, but its files can
    /// still be the ones the redirect resolves; skipped reads are not
    /// evidence that the package resolves from the immutable package.
    /// </summary>
    [Fact]
    public void AStageWhoseMarkerIsGoneReportsAnUnknownResolution()
    {
        string application = NativeStagePackageFixture.WriteApplication(
            Path.Combine(_root, "package"),
            new Dictionary<string, string>
            {
                ["node_modules/koffi/package.json"] = "{\"version\":\"2.0.0\"}",
                ["node_modules/koffi/koffi.node"] = "native"
            });
        string root = SessionNativeStager.Stage(application, Path.Combine(_root, "local"))!;
        File.Delete(Path.Combine(root, ".staged-content-id"));

        SessionNativeStageReport report = SessionNativeStageInspector.Inspect(application, root);

        Assert.Equal("missing", report.State);
        Assert.Equal("unknown", Assert.Single(report.Packages).ResolvesFrom);
    }

    [Fact]
    public void ANativeFreeApplicationWithoutAStageNeedsNone()
    {
        string application = NativeStagePackageFixture.WriteApplication(
            Path.Combine(_root, "package"),
            new Dictionary<string, string> { ["node_modules/lodash/index.js"] = "js only" });

        SessionNativeStageReport report = SessionNativeStageInspector.Inspect(application, rootPath: null);

        Assert.Equal("not-required", report.State);
        Assert.Empty(report.Packages);
    }
}
