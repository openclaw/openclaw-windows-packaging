using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcPackageMetadataTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public void MetadataIsDescribedWithoutInspectingOrLoadingRuntimeAssets()
    {
        File.WriteAllText(Path.Combine(_root, MxcRuntimeLocator.ProvenanceFileName),
            """{"package":"Microsoft.Mxc.Sdk","version":"1.0.1","architecture":"x64"}""");

        MxcRuntimeLocation result = MxcRuntimeLocator.Locate(_root);

        Assert.Equal(_root, result.Directory);
        Assert.Equal("Microsoft.Mxc.Sdk", result.Provenance?.Package);
        Assert.Equal("1.0.1", result.Provenance?.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{}")]
    public void OptionalDescriptiveMetadataDoesNotDecideSdkAvailability(string? content)
    {
        if (content is not null)
        {
            File.WriteAllText(Path.Combine(_root, MxcRuntimeLocator.ProvenanceFileName), content);
        }

        MxcRuntimeLocation result = MxcRuntimeLocator.Locate(_root);

        Assert.Equal(_root, result.Directory);
        Assert.Null(result.Provenance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
