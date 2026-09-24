using System.Globalization;
using System.Runtime.InteropServices;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher;

/// <summary>
/// The Windows build and the version of every component in this
/// installation's execution chain.
/// </summary>
/// <remarks>
/// Recorded when the host starts and in each diagnostics bundle, so a report
/// names the build and dependency chain that produced it without the reporter
/// collecting them by hand. Every fact is best-effort: one that cannot be read
/// is described as unavailable instead of failing the command being diagnosed.
/// Reading them costs no process launch, which is why the MXC backend probe is
/// recorded where it already runs rather than here.
/// </remarks>
internal sealed record HostEnvironment(
    string Windows,
    string Package,
    string Mxc,
    string NodeJs,
    string DotNet)
{
    public static HostEnvironment Read(
        string baseDirectory,
        string? nodeArchivePath,
        Func<string, string?> readEnvironmentVariable) =>
        new(
            DescribeWindows(
                Environment.OSVersion.Version,
                WindowsHostBuild.TryRead(),
                RuntimeInformation.OSArchitecture,
                RuntimeInformation.ProcessArchitecture),
            DescribeInstalledPackage(),
            DescribeMxc(baseDirectory, readEnvironmentVariable),
            DescribeNodeJs(nodeArchivePath, RuntimeInformation.ProcessArchitecture),
            RuntimeInformation.FrameworkDescription);

    public string Describe() =>
        $"Windows {Windows}; package {Package}, build " +
        $"{ClawCtlBuildMetadata.PackageVersion} (commit {ClawCtlBuildMetadata.PackageCommit}); " +
        $"OpenClaw payload {ClawCtlBuildMetadata.PayloadVersion} " +
        $"(commit {ClawCtlBuildMetadata.PayloadCommit}); MXC {Mxc}; Node.js {NodeJs}; {DotNet}";

    internal static string DescribeWindows(
        Version osVersion,
        MxcHostBuild? hostBuild,
        Architecture osArchitecture,
        Architecture processArchitecture)
    {
        string version = hostBuild is null
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{osVersion.Major}.{osVersion.Minor}.{osVersion.Build} (update revision unavailable)")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{osVersion.Major}.{osVersion.Minor}.{hostBuild.Build}.{hostBuild.UpdateBuildRevision}");

        // They differ under emulation, and the process architecture is the one
        // that selects the staged MXC runtime and Node.js archive.
        return $"{version} ({osArchitecture} OS, {processArchitecture} process)";
    }

    internal static string DescribeMxc(
        string baseDirectory,
        Func<string, string?> readEnvironmentVariable)
    {
        MxcRuntimeLocation location;
        try
        {
            location = MxcRuntimeLocator.Locate(baseDirectory, readEnvironmentVariable);
        }
        catch (MxcException exception)
        {
            return $"unavailable ({exception.Message})";
        }

        string runtime = location.Provenance is { } provenance
            ? $"{provenance.Package} {provenance.Version} {provenance.Architecture}"
            : "runtime of unknown provenance";
        string source = string.IsNullOrWhiteSpace(
            readEnvironmentVariable(MxcRuntimeLocator.RuntimeDirectoryVariable))
                ? string.Empty
                : $", overridden by {MxcRuntimeLocator.RuntimeDirectoryVariable} " +
                  $"to {location.Directory}";
        return $"{runtime}, wire {MxcWireProtocol.IsolationSessionSchemaVersion}{source}";
    }

    internal static string DescribeNodeJs(string? archivePath, Architecture architecture)
    {
        if (archivePath is null)
        {
            return "archive missing";
        }

        try
        {
            return NodeRuntimeInstaller.GetArchiveVersion(archivePath, architecture).ToString();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or PlatformNotSupportedException)
        {
            return $"archive {Path.GetFileName(archivePath)} unrecognized";
        }
    }

    /// <summary>
    /// Names how the package was installed: a loose layout registered in
    /// Developer Mode, a Store-signed install, or a sideloaded MSIX and the
    /// kind of signature Windows accepted it with.
    /// </summary>
    internal static string DescribePackage(PackageProvenance? provenance)
    {
        if (provenance is null)
        {
            return "unpackaged";
        }

        // Development mode is checked first: a loose registration is unsigned
        // whatever the certificate its checkout would sign with.
        string kind = provenance.DevelopmentMode == true
            ? "Developer Mode loose-layout registration"
            : provenance.Origin switch
            {
                PackageOrigin.Store => "Store-signed",
                PackageOrigin.DeveloperSigned => "developer-signed MSIX",
                PackageOrigin.LineOfBusiness => "line-of-business MSIX",
                PackageOrigin.Unsigned => "unsigned MSIX",
                PackageOrigin.DeveloperUnsigned => "unsigned developer registration",
                PackageOrigin.Inbox => "inbox package",
                null => "origin unreadable",
                _ => "unknown origin"
            };
        string origin = provenance.Origin is { } value ? $", origin {value}" : string.Empty;
        string developmentMode = provenance.DevelopmentMode is null
            ? ", development mode unreadable"
            : string.Empty;
        string location = provenance.InstallPath is { } path
            ? $" at {path}"
            : " at an unreadable location";
        return $"{provenance.FullName} ({kind}{origin}{developmentMode}){location}";
    }

    private static string DescribeInstalledPackage()
    {
        try
        {
            return DescribePackage(PackageIdentity.TryReadProvenance());
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            EntryPointNotFoundException or
            DllNotFoundException)
        {
            // A diagnostic read must never fail the command it describes.
            return $"identity unreadable ({exception.Message})";
        }
    }
}
