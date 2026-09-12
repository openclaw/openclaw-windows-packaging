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
            {"package":"@microsoft/mxc-sdk","version":"0.8.0","architecture":"x64"}
            """);

        MxcRuntimeLocation location = MxcRuntimeLocator.Locate(
            _testDirectory,
            NoEnvironment);

        Assert.Equal(runtimeDirectory, location.Directory);
        Assert.Equal(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.ExecutorFileName),
            location.ExecutorPath);
        Assert.Equal("@microsoft/mxc-sdk", location.Provenance?.Package);
        Assert.Equal("0.8.0", location.Provenance?.Version);
    }

    [Fact]
    public void AnExplicitRuntimeDirectoryOverridesThePackagedLayout()
    {
        string runtimeDirectory = StageRuntime(
            Path.Combine(_testDirectory, "experiment"));

        MxcRuntimeLocation location = MxcRuntimeLocator.Locate(
            _testDirectory,
            name => name == MxcRuntimeLocator.RuntimeDirectoryVariable
                ? runtimeDirectory
                : null);

        Assert.Equal(runtimeDirectory, location.Directory);
    }

    [Fact]
    public void AMissingRuntimeNamesTheExpectedExecutorPath()
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcRuntimeLocator.Locate(_testDirectory, NoEnvironment));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, exception.Code);
        Assert.Contains(
            MxcRuntimeLocator.ExecutorFileName,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompleteRuntimeIsRejectedRatherThanPartiallyUsed()
    {
        string runtimeDirectory = Path.Combine(_testDirectory, "experiment");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.ExecutorFileName),
            string.Empty);

        MxcException exception = Assert.Throws<MxcException>(
            () => MxcRuntimeLocator.Locate(
                _testDirectory,
                name => name == MxcRuntimeLocator.RuntimeDirectoryVariable
                    ? runtimeDirectory
                    : null));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, exception.Code);
        Assert.Contains(
            MxcRuntimeLocator.PackageLifecycleFileName,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"package":"@microsoft/mxc-sdk"}""")]
    public void DamagedProvenanceLeavesTheVerifiedRuntimeUsable(string provenance)
    {
        // Provenance is descriptive. The binaries were verified at staging
        // time, so an unreadable record must not disable isolated sessions.
        string runtimeDirectory = StageRuntime(
            Path.Combine(_testDirectory, "experiment"));
        File.WriteAllText(
            Path.Combine(runtimeDirectory, MxcRuntimeLocator.ProvenanceFileName),
            provenance);

        MxcRuntimeLocation location = MxcRuntimeLocator.Locate(
            _testDirectory,
            name => name == MxcRuntimeLocator.RuntimeDirectoryVariable
                ? runtimeDirectory
                : null);

        Assert.Null(location.Provenance);
    }

    private static string StageRuntime(string runtimeDirectory)
    {
        Directory.CreateDirectory(runtimeDirectory);
        foreach (string fileName in new[]
        {
            MxcRuntimeLocator.ExecutorFileName,
            MxcRuntimeLocator.PackageLifecycleFileName
        })
        {
            File.WriteAllText(
                Path.Combine(runtimeDirectory, fileName),
                string.Empty);
        }

        return runtimeDirectory;
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
