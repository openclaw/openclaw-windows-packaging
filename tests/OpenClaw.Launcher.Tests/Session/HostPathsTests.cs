
namespace OpenClaw.Launcher.Tests.Session;

public sealed class HostPathsTests
{
    [Fact]
    public void UnpackagedStateRootLivesUnderLocalApplicationData()
    {
        HostPaths paths = HostPaths.Create(@"C:\Users\test\AppData\Local", null);

        Assert.Equal(
            @"C:\Users\test\AppData\Local\OpenClawGatewayMSIX",
            paths.StateRoot);
        Assert.Null(paths.PackageFamilyName);
    }

    [Fact]
    public void PackagedStateRootLivesInsidePackageLocalState()
    {
        HostPaths paths = HostPaths.Create(
            @"C:\Users\test\AppData\Local",
            "OpenClaw.Gateway_abc123");

        Assert.Equal(
            @"C:\Users\test\AppData\Local\Packages\OpenClaw.Gateway_abc123\LocalState\OpenClawGatewayMSIX",
            paths.StateRoot);
        Assert.Equal("OpenClaw.Gateway_abc123", paths.PackageFamilyName);
    }

    [Fact]
    public void DifferentPackageIdentitiesDoNotShareState()
    {
        HostPaths first = HostPaths.Create(@"C:\local", "Public_abc");
        HostPaths second = HostPaths.Create(@"C:\local", "Internal_xyz");

        Assert.NotEqual(first.StateRoot, second.StateRoot);
        Assert.NotEqual(first.SessionStatePath, second.SessionStatePath);
    }

    [Fact]
    public void DerivedPathsSitUnderTheStateRoot()
    {
        HostPaths paths = HostPaths.Create(@"C:\local", null);

        Assert.StartsWith(paths.StateRoot, paths.LogPath, StringComparison.Ordinal);
        Assert.StartsWith(paths.StateRoot, paths.SessionStatePath, StringComparison.Ordinal);
        Assert.NotEqual(paths.LogPath, paths.SessionStatePath);
    }

    [Fact]
    public void MissingLocalApplicationDataIsReported()
    {
        Assert.Throws<InvalidOperationException>(
            () => HostPaths.Create(string.Empty, null));
    }

    [Fact]
    public void ApplicationIdCarriesThePackageFamilyNamePrefix()
    {
        Assert.Equal(
            "PFN:OpenClaw.Gateway_abc123",
            PackageIdentity.ToApplicationId("OpenClaw.Gateway_abc123"));
    }

    [Fact]
    public void PackageFamilyNameIsAbsentWhenRunningUnpackaged()
    {
        // The test host is never packaged, so this also proves that unpackaged
        // is reported rather than thrown.
        Assert.Null(PackageIdentity.TryGetPackageFamilyName());
    }

    [Fact]
    public void ApplicationUserModelIdIsAbsentWithoutAnApplicationIdentity()
    {
        Assert.Null(PackageIdentity.TryGetApplicationUserModelId());
    }
}
