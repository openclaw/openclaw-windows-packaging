using System.Runtime.InteropServices;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests;

public sealed class HostEnvironmentTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // The update revision is what the documented IsolationSession minimum is
    // stated in, so a build without it cannot be compared to that minimum.
    [Fact]
    public void WindowsIsReportedWithItsUpdateRevision()
    {
        Assert.Equal(
            "10.0.26340.9212 (X64 OS, X64 process)",
            HostEnvironment.DescribeWindows(
                new Version(10, 0, 26340),
                new MxcHostBuild(26340, 9212),
                Architecture.X64,
                Architecture.X64));
    }

    [Fact]
    public void AnUnreadableUpdateRevisionIsNamedRatherThanGuessed()
    {
        Assert.Equal(
            "10.0.26340 (update revision unavailable) (X64 OS, X64 process)",
            HostEnvironment.DescribeWindows(
                new Version(10, 0, 26340),
                null,
                Architecture.X64,
                Architecture.X64));
    }

    // Emulation changes which staged runtime and Node.js archive run.
    [Fact]
    public void AnEmulatedProcessIsDistinguishedFromItsHost()
    {
        Assert.EndsWith(
            "(Arm64 OS, X64 process)",
            HostEnvironment.DescribeWindows(
                new Version(10, 0, 26340),
                new MxcHostBuild(26340, 9212),
                Architecture.Arm64,
                Architecture.X64),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheStagedMxcRuntimeIsNamedByItsProvenanceAndWireSchema()
    {
        StageMxcRuntime(Path.Combine(
            _root,
            MxcRuntimeLocator.RuntimeDirectoryName,
            MxcRuntimeLocator.CurrentArchitectureName()),
            provenance: true);

        Assert.Equal(
            $"@microsoft/mxc-sdk 0.8.0 {MxcRuntimeLocator.CurrentArchitectureName()}, " +
            $"wire {MxcWireProtocol.IsolationSessionSchemaVersion}",
            HostEnvironment.DescribeMxc(_root, _ => null));
    }

    [Fact]
    public void AnOverriddenMxcRuntimeNamesWhereItCameFrom()
    {
        string overrideDirectory = Path.Combine(_root, "experiment");
        StageMxcRuntime(overrideDirectory, provenance: false);

        Assert.Equal(
            "runtime of unknown provenance, " +
            $"wire {MxcWireProtocol.IsolationSessionSchemaVersion}, " +
            $"overridden by {MxcRuntimeLocator.RuntimeDirectoryVariable} to {overrideDirectory}",
            HostEnvironment.DescribeMxc(
                _root,
                name => name == MxcRuntimeLocator.RuntimeDirectoryVariable
                    ? overrideDirectory
                    : null));
    }

    [Fact]
    public void AMissingMxcRuntimeIsReportedWithoutFailing()
    {
        Assert.StartsWith(
            "unavailable (The MXC runtime is not available: ",
            HostEnvironment.DescribeMxc(_root, _ => null),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheNodeRuntimeIsNamedByTheArchiveTheProcessWouldInstall()
    {
        Assert.Equal(
            "24.20.0",
            HostEnvironment.DescribeNodeJs(
                @"C:\package\runtime\node-v24.20.0-win-x64.zip",
                Architecture.X64));
        Assert.Equal(
            "archive node-v24.20.0-win-x64.zip unrecognized",
            HostEnvironment.DescribeNodeJs(
                @"C:\package\runtime\node-v24.20.0-win-x64.zip",
                Architecture.Arm64));
        Assert.Equal("archive missing", HostEnvironment.DescribeNodeJs(null, Architecture.X64));
    }

    // A Store install, a test-signed MSIX, and a loose registration of one
    // checkout share a family name; only provenance tells a bundle which ran.
    [Theory]
    [InlineData(
        nameof(PackageOrigin.DeveloperUnsigned),
        true,
        @"E:\repo\openclaw-windows-packaging\artifacts\local-package\x64\layout",
        "(Developer Mode loose-layout registration, origin DeveloperUnsigned) at " +
        @"E:\repo\openclaw-windows-packaging\artifacts\local-package\x64\layout")]
    [InlineData(
        nameof(PackageOrigin.Store),
        false,
        @"C:\Program Files\WindowsApps\fixture",
        @"(Store-signed, origin Store) at C:\Program Files\WindowsApps\fixture")]
    [InlineData(
        nameof(PackageOrigin.DeveloperSigned),
        false,
        @"C:\Program Files\WindowsApps\fixture",
        @"(developer-signed MSIX, origin DeveloperSigned) at C:\Program Files\WindowsApps\fixture")]
    [InlineData(
        nameof(PackageOrigin.LineOfBusiness),
        false,
        @"C:\Program Files\WindowsApps\fixture",
        @"(line-of-business MSIX, origin LineOfBusiness) at C:\Program Files\WindowsApps\fixture")]
    public void EachInstallKindIsNamed(
        string origin,
        bool developmentMode,
        string installPath,
        string expected)
    {
        const string fullName = "OpenClawFoundation.OpenClawGateway_2026.9.4.1004_x64__rfcbke2p71se2";

        Assert.Equal(
            $"{fullName} {expected}",
            HostEnvironment.DescribePackage(new PackageProvenance(
                fullName,
                Enum.Parse<PackageOrigin>(origin),
                developmentMode,
                installPath)));
    }

    [Fact]
    public void ProvenanceWindowsCouldNotReportIsNamedAsUnreadable()
    {
        Assert.Equal(
            "OpenClaw.Gateway_2026.9.4.1003_x64__kaa03rpbbqef6 " +
            "(origin unreadable, development mode unreadable) at an unreadable location",
            HostEnvironment.DescribePackage(new PackageProvenance(
                "OpenClaw.Gateway_2026.9.4.1003_x64__kaa03rpbbqef6",
                null,
                null,
                null)));
        Assert.Equal("unpackaged", HostEnvironment.DescribePackage(null));
    }

    // The test host is not packaged, so this cannot observe a real origin. It
    // does prove each AppModel entry point binds: the origin export lives in
    // kernelbase.dll, and a wrong library or name fails only at first call.
    [Fact]
    public void AnUnpackagedProcessHasNoProvenance()
    {
        Assert.Null(PackageIdentity.TryReadProvenance());
        Assert.Null(PackageIdentity.TryReadOrigin(
            "OpenClaw.NotInstalled_1.0.0.0_x64__0000000000000"));
        Assert.Null(PackageIdentity.TryReadDevelopmentMode());
        Assert.Null(PackageIdentity.TryReadInstallPath());
    }

    [Fact]
    public void TheDescriptionCarriesTheCompiledBuildIdentity()
    {
        var environment = new HostEnvironment(
            "10.0.26340.9212 (X64 OS, X64 process)",
            "unpackaged",
            "unavailable (missing)",
            "archive missing",
            ".NET fixture");

        Assert.Equal(
            "Windows 10.0.26340.9212 (X64 OS, X64 process); package unpackaged, build " +
            $"{ClawCtlBuildMetadata.PackageVersion} (commit {ClawCtlBuildMetadata.PackageCommit}); " +
            $"OpenClaw payload {ClawCtlBuildMetadata.PayloadVersion} " +
            $"(commit {ClawCtlBuildMetadata.PayloadCommit}); MXC unavailable (missing); " +
            "Node.js archive missing; .NET fixture",
            environment.Describe());
    }

    private static void StageMxcRuntime(string directory, bool provenance)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, MxcRuntimeLocator.ExecutorFileName), "fixture");
        File.WriteAllText(
            Path.Combine(directory, MxcRuntimeLocator.PackageLifecycleFileName),
            "fixture");
        if (provenance)
        {
            string architecture = MxcRuntimeLocator.CurrentArchitectureName();
            File.WriteAllText(
                Path.Combine(directory, MxcRuntimeLocator.ProvenanceFileName),
                $$"""
                {"package":"@microsoft/mxc-sdk","version":"0.8.0","architecture":"{{architecture}}"}
                """);
        }
    }
}
