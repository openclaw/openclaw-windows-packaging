using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcRuntimeLocatorTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();
    private readonly List<string> _cleared = [];

    [Fact]
    public void NativeUnitBesideTheLauncherIsResolved()
    {
        StageNativeUnit(
            MxcRuntimeLocator.NativeLibraryFileName,
            MxcRuntimeLocator.PackageLifecycleFileName);

        MxcRuntimeLocation location = MxcRuntimeLocator.Locate(_testDirectory, _cleared.Add);

        Assert.Equal(Path.GetFullPath(_testDirectory), location.Directory);
        Assert.Equal(
            Path.Combine(_testDirectory, MxcRuntimeLocator.NativeLibraryFileName),
            location.NativeLibraryPath);
        Assert.Equal(
            Path.Combine(_testDirectory, MxcRuntimeLocator.PackageLifecycleFileName),
            location.PackageLifecyclePath);
    }

    [Fact]
    public void ResolvingTheNativeUnitRemovesTheSdkLoadOverride()
    {
        // The SDK probes MXC_FFI_DIR ahead of the application base, so leaving
        // it set would let any directory service a managed session.
        StageNativeUnit(
            MxcRuntimeLocator.NativeLibraryFileName,
            MxcRuntimeLocator.PackageLifecycleFileName);

        _ = MxcRuntimeLocator.Locate(_testDirectory, _cleared.Add);

        Assert.Equal([MxcRuntimeLocator.NativeDirectoryOverrideVariable], _cleared);
    }

    [Fact]
    public void AMissingNativeLibraryNamesTheExpectedPath()
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcRuntimeLocator.Locate(_testDirectory, _cleared.Add));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, exception.Code);
        Assert.Contains(
            Path.Combine(_testDirectory, MxcRuntimeLocator.NativeLibraryFileName),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompleteNativeUnitIsRejectedRatherThanPartiallyUsed()
    {
        // The engine resolves plm.exe beside the loaded library, so a library
        // without it would fail later and further from the cause.
        StageNativeUnit(MxcRuntimeLocator.NativeLibraryFileName);

        MxcException exception = Assert.Throws<MxcException>(
            () => MxcRuntimeLocator.Locate(_testDirectory, _cleared.Add));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, exception.Code);
        Assert.Contains(
            MxcRuntimeLocator.PackageLifecycleFileName,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(_cleared);
    }

    private void StageNativeUnit(params string[] fileNames)
    {
        foreach (string fileName in fileNames)
        {
            File.WriteAllText(Path.Combine(_testDirectory, fileName), string.Empty);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
