using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcRuntimeLocatorTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public void PublishedNativeUnitIsLocatedBesideTheLauncherWithoutLoadingIt()
    {
        Stage(_root);
        File.WriteAllText(Path.Combine(_root, MxcRuntimeLocator.ProvenanceFileName),
            """{"package":"Microsoft.Mxc.Sdk","version":"1.0.0","architecture":"x64"}""");

        MxcRuntimeLocation result = MxcRuntimeLocator.Locate(_root);

        Assert.Equal(_root, result.Directory);
        Assert.Equal(Path.Combine(_root, "mxc_ffi.dll"), result.NativeLibraryPath);
        Assert.Equal(Path.Combine(_root, "plm.exe"), result.PackageLifecyclePath);
        Assert.Equal("Microsoft.Mxc.Sdk", result.Provenance?.Package);
        Assert.Equal("1.0.0", result.Provenance?.Version);
    }

    [Fact]
    public void OrdinaryNuGetRidLayoutUsesTheProcessArchitecture()
    {
        string directory = Path.Combine(
            _root, "runtimes", $"win-{MxcRuntimeLocator.CurrentArchitectureName()}", "native");
        Stage(directory);

        Assert.Equal(directory, MxcRuntimeLocator.Locate(_root).Directory);
    }

    [Theory]
    [InlineData("mxc_ffi.dll")]
    [InlineData("plm.exe")]
    [InlineData("wslcsdk.dll")]
    [InlineData("wxc-wslc-daemon.exe")]
    public void AnIncompleteUnitIsRefused(string missing)
    {
        Stage(_root);
        File.Delete(Path.Combine(_root, missing));

        MxcException failure = Assert.Throws<MxcException>(() => MxcRuntimeLocator.Locate(_root));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, failure.Code);
        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"package":"Microsoft.Mxc.Sdk"}""")]
    public void DamagedDescriptiveProvenanceDoesNotAuthorizeOrDisableTheUnit(string json)
    {
        Stage(_root);
        File.WriteAllText(Path.Combine(_root, MxcRuntimeLocator.ProvenanceFileName), json);

        Assert.Null(MxcRuntimeLocator.Locate(_root).Provenance);
    }

    private static void Stage(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "mxc_ffi.dll", "plm.exe", "wslcsdk.dll", "wxc-wslc-daemon.exe" })
        {
            File.WriteAllText(Path.Combine(directory, name), "fixture, not a native library");
        }
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
