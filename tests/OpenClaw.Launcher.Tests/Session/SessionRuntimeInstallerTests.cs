using System.IO.Compression;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Drives the real guest installer against real files.
/// </summary>
/// <remarks>
/// The whole contract is that a failure arrives as a readable result file: the
/// host has no other way to say why an install failed, and an unhandled
/// exception in the guest reports itself as a lost request instead.
/// </remarks>
public sealed class SessionRuntimeInstallerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string RequestPath => Path.Combine(_root, "runtime.json");

    private SessionRuntimeInstallResult Run(string archivePath)
    {
        File.WriteAllText(
            RequestPath,
            SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
            {
                RequestId = "r1",
                ArchivePath = archivePath,

                // The account running tests is the developer's own, and its PATH
                // is not this test's to change.
                UpdateUserPath = false
            }));

        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            File.ReadAllText,
            File.WriteAllText,
            () => _root,
            getRuntimeVersion: ReadFixtureVersion);
        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);

        return SessionRuntimeProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(RequestPath)));
    }

    private SessionRuntimeInstallResult Install(string archivePath)
    {
        File.WriteAllText(
            RequestPath,
            SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
            {
                RequestId = "r1",
                ArchivePath = archivePath,
                UpdateUserPath = false
            }));

        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            File.ReadAllText,
            File.WriteAllText,
            () => _root,
            getRuntimeVersion: ReadFixtureVersion);
        Assert.Equal(0, exitCode);

        return SessionRuntimeProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(RequestPath)));
    }

    private string CreateArchive(string version)
    {
        string archivePath = Path.Combine(_root, $"node-v{version}-win-x64.zip");
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry(
            $"node-v{version}-win-x64/node.exe");
        using StreamWriter writer = new(entry.Open());
        writer.Write(version);
        return archivePath;
    }

    private static string? ReadFixtureVersion(string executablePath) =>
        File.Exists(executablePath) ? File.ReadAllText(executablePath) : null;

    [Fact]
    public void ReinstallingIntoAnExistingAgentProfileReportsTheCurrentArchiveVersion()
    {
        Install(CreateArchive("24.15.0"));

        SessionRuntimeInstallResult result = Install(CreateArchive("24.20.0"));

        Assert.Equal("24.20.0", result.Version);
        Assert.Equal("node-v24.20.0-win-x64.zip", result.ArchiveName);
        Assert.Equal(
            Path.Combine(
                _root,
                "OpenClawGatewayMSIX",
                "agent-node",
                "node-v24.20.0-win-x64",
                "node.exe"),
            result.ExecutablePath);
        Assert.Equal("24.20.0", File.ReadAllText(result.ExecutablePath!));
    }

    [Fact]
    public void InstallPersistsTheDirectoryContainingNode()
    {
        string archivePath = CreateArchive("24.20.0");
        File.WriteAllText(
            RequestPath,
            SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
            {
                RequestId = "r1",
                ArchivePath = archivePath
            }));
        string? persistedDirectory = null;

        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            File.ReadAllText,
            File.WriteAllText,
            () => _root,
            directory =>
            {
                persistedDirectory = directory;
                return true;
            },
            ReadFixtureVersion);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            Path.Combine(
                _root,
                "OpenClawGatewayMSIX",
                "agent-node",
                "node-v24.20.0-win-x64"),
            persistedDirectory);
    }

    [Fact]
    public void SameVersionInstallReusesExistingNodeEvenWhenItIsOpen()
    {
        string archivePath = CreateArchive("24.20.0");
        SessionRuntimeInstallResult first = Install(archivePath);
        using FileStream heldOpen = File.Open(
            first.ExecutablePath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        SessionRuntimeInstallResult second = Install(archivePath);

        Assert.Equal(first.ExecutablePath, second.ExecutablePath);
        Assert.Equal("24.20.0", second.Version);
    }

    [Fact]
    public void SameVersionInstallRepairsAnInvalidExistingNode()
    {
        string archivePath = CreateArchive("24.20.0");
        SessionRuntimeInstallResult first = Install(archivePath);
        File.WriteAllText(first.ExecutablePath!, "broken");

        SessionRuntimeInstallResult second = Install(archivePath);

        Assert.Equal(first.ExecutablePath, second.ExecutablePath);
        Assert.Equal("24.20.0", File.ReadAllText(second.ExecutablePath!));
    }

    [Fact]
    public void ProductionProbeRepairsAnUnlaunchableExistingNode()
    {
        string archivePath = CreateArchive("24.20.0");
        SessionRuntimeInstallResult first = Install(archivePath);
        File.WriteAllText(first.ExecutablePath!, "not an executable");
        File.WriteAllText(
            RequestPath,
            SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
            {
                RequestId = "r1",
                ArchivePath = archivePath,
                UpdateUserPath = false
            }));

        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            File.ReadAllText,
            File.WriteAllText,
            () => _root);

        Assert.Equal(0, exitCode);
        Assert.Equal("24.20.0", File.ReadAllText(first.ExecutablePath!));
    }

    // A truncated or corrupt archive is a real packaging failure. Left
    // unhandled it crashes the guest, and the host then reports a lost request
    // rather than the reason.
    [Fact]
    public void ACorruptArchiveIsReportedThroughTheResultFile()
    {
        string archive = Path.Combine(_root, "node-v24.15.0-win-x64.zip");
        File.WriteAllText(archive, "this is not a zip archive");

        SessionRuntimeInstallResult result = Run(archive);

        Assert.NotNull(result.Error);
        Assert.Null(result.ExecutablePath);
    }

    [Fact]
    public void AMissingArchiveIsReportedThroughTheResultFile()
    {
        SessionRuntimeInstallResult result = Run(
            Path.Combine(_root, "node-v24.15.0-win-x64.zip"));

        Assert.NotNull(result.Error);
        Assert.Null(result.ExecutablePath);
    }

    // An archive whose name does not carry a version cannot be installed, and
    // saying so beats extracting something unidentifiable.
    [Fact]
    public void AnUnrecognizedArchiveNameIsReportedThroughTheResultFile()
    {
        string archive = Path.Combine(_root, "node.zip");
        File.WriteAllText(archive, "irrelevant");

        SessionRuntimeInstallResult result = Run(archive);

        Assert.NotNull(result.Error);
    }
}
