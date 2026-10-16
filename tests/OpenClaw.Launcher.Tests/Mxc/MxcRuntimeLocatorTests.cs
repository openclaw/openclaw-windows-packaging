using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcRuntimeLocatorTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void RuntimeIsResolvedFromTheArchitectureSpecificPackageDirectory()
    {
        string runtimeDirectory = StageRuntime(
            Path.Combine(
                _testDirectory,
                MxcRuntimeLocator.RuntimeDirectoryName,
                MxcRuntimeLocator.CurrentArchitectureName()));
        File.WriteAllText(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.ProvenanceFileName),
            """
            {"package":"Microsoft.Mxc.Sdk","version":"1.0.0","architecture":"x64"}
            """);

        MxcRuntimeLocation location = MxcRuntimeLocator.Locate(
            _testDirectory,
            NoEnvironment);

        Assert.Equal(runtimeDirectory, location.Directory);
        Assert.Equal(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.NativeLibraryFileName),
            location.LibraryPath);
        Assert.Equal("Microsoft.Mxc.Sdk", location.Provenance?.Package);
        Assert.Equal("1.0.0", location.Provenance?.Version);
    }

    [Fact]
    public void AnAmbientRuntimeDirectoryCannotReplaceThePackagedSdk()
    {
        string runtimeDirectory = StageRuntime(
            Path.Combine(_testDirectory, "experiment"));

        MxcException exception = Assert.Throws<MxcException>(() => MxcRuntimeLocator.Locate(
            _testDirectory,
            name => name == MxcRuntimeLocator.RuntimeDirectoryVariable
                ? runtimeDirectory
                : null));

        Assert.Equal(MxcErrorCode.ConfigurationRejected, exception.Code);
    }

    [Fact]
    public void AMissingRuntimeNamesTheExpectedNativeLibraryPath()
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcRuntimeLocator.Locate(_testDirectory, NoEnvironment));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, exception.Code);
        Assert.Contains(
            MxcRuntimeLocator.NativeLibraryFileName,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"package":"Microsoft.Mxc.Sdk"}""")]
    public void DamagedProvenanceLeavesRuntimeDiscoveryAvailable(string provenance)
    {
        // Discovery is diagnostic; initialization separately enforces the compiled native identity.
        string runtimeDirectory = StageRuntime(
            Path.Combine(_testDirectory, MxcRuntimeLocator.RuntimeDirectoryName,
                MxcRuntimeLocator.CurrentArchitectureName()));
        File.WriteAllText(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.ProvenanceFileName),
            provenance);

        MxcRuntimeLocation location = MxcRuntimeLocator.Locate(
            _testDirectory,
            NoEnvironment);

        Assert.Null(location.Provenance);
    }

    private static string StageRuntime(string runtimeDirectory)
    {
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.NativeLibraryFileName),
            string.Empty);

        return runtimeDirectory;
    }

    [Fact]
    public void NativeInitializationRejectsTamperedBytesBeforeLoading()
    {
        StageRuntime(Path.Combine(_testDirectory, MxcRuntimeLocator.RuntimeDirectoryName,
            MxcRuntimeLocator.CurrentArchitectureName()));
        MxcRuntimeLocation location = MxcRuntimeLocator.Locate(_testDirectory, NoEnvironment);
        MxcException exception = Assert.Throws<MxcException>(() =>
            MxcRuntimeLocator.InitializeSdkRuntime(location));
        Assert.Equal(MxcErrorCode.RuntimeUnavailable, exception.Code);
        Assert.Contains("pinned identity", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
