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

    /// <summary>
    /// An application directory with no <c>node_modules</c>, so these tests
    /// exercise the runtime install without staging native dependencies.
    /// </summary>
    private string ApplicationDirectory => Path.Combine(_root, "app");

    private SessionRuntimeInstallResult Run(string archivePath, TextWriter? errorOutput = null)
    {
        File.WriteAllText(
            RequestPath,
            SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
            {
                RequestId = "r1",
                ArchivePath = archivePath,
                ApplicationDirectory = ApplicationDirectory,

                // The account running tests is the developer's own, and its PATH
                // is not this test's to change.
                UpdateUserPath = false
            }));

        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            File.ReadAllText,
            File.WriteAllText,
            () => _root,
            getRuntimeVersion: ReadFixtureVersion,
            errorOutput: errorOutput);
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
                ApplicationDirectory = ApplicationDirectory,
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
                ArchivePath = archivePath,
                ApplicationDirectory = ApplicationDirectory
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
    public void ProductionProbeRepairsAnUnlaunchableExistingNode() =>
        AssertProductionProbeRepairs(File.WriteAllText, "not an executable");

    // A DOS-style "MZ" stub with no PE header is the image the Windows loader
    // reports as an unsupported 16-bit application, with a desktop dialog on
    // an interactive session. The probe must reject it without launching it.
    [Fact]
    public void ProductionProbeRepairsAnExistingNodeWithOnlyADosHeader() =>
        AssertProductionProbeRepairs(
            (path, _) => File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]),
            string.Empty);

    private void AssertProductionProbeRepairs(Action<string, string> corrupt, string content)
    {
        string archivePath = CreateArchive("24.20.0");
        SessionRuntimeInstallResult first = Install(archivePath);
        corrupt(first.ExecutablePath!, content);
        File.WriteAllText(
            RequestPath,
            SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
            {
                RequestId = "r1",
                ArchivePath = archivePath,
                ApplicationDirectory = ApplicationDirectory,
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
        var errors = new StringWriter();
        SessionRuntimeInstallResult result = Run(
            Path.Combine(_root, "node-v24.15.0-win-x64.zip"),
            errors);

        Assert.Equal("r1", result.RequestId);
        Assert.NotNull(result.Error);
        Assert.Null(result.ExecutablePath);
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public void AnUnwritableFailureResultRetainsBothFailuresOnRedactedStderr()
    {
        const string requestId = "runtime-write-failure";
        string archivePath = Path.Combine(_root, "node-v24.15.0-win-x64.zip");
        string operationPath = Path.Combine(_root, "private profile", "runtime.zip");
        string operationError =
            $"runtime operation failed for '{operationPath}' OPENAI_API_KEY=operation-secret";
        string publicationError =
            $"failure result denied for '{RequestPath}.result.json' GATEWAY_TOKEN=write-secret";
        File.WriteAllText(
            RequestPath,
            SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
            {
                RequestId = requestId,
                ArchivePath = archivePath,
                ApplicationDirectory = ApplicationDirectory,
                UpdateUserPath = false
            }));
        string? attemptedResult = null;
        var errors = new StringWriter();

        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            File.ReadAllText,
            (path, contents) =>
            {
                Assert.Equal(SessionLaunchProtocol.ResultPathFor(RequestPath), path);
                attemptedResult = contents;
                throw new UnauthorizedAccessException(publicationError);
            },
            getLocalApplicationData: () => throw new IOException(operationError),
            errorOutput: errors);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        SessionRuntimeInstallResult result = SessionRuntimeProtocol.ReadResult(
            Assert.IsType<string>(attemptedResult));
        Assert.Equal(requestId, result.RequestId);
        Assert.Equal(operationError, result.Error);

        string output = errors.ToString();
        Assert.Contains(requestId, output, StringComparison.Ordinal);
        Assert.Contains("runtime operation failed", output, StringComparison.Ordinal);
        Assert.Contains("failure result denied", output, StringComparison.Ordinal);
        Assert.Contains(operationPath, output, StringComparison.Ordinal);
        Assert.Contains("<redacted>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("operation-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("write-secret", output, StringComparison.Ordinal);
        _ = Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.True(output.Length < 1200);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedSuccessResultWriteReportsThePublicationFailure(bool failureWriteDenied)
    {
        const string requestId = "runtime-success-write-failure";
        string archivePath = CreateArchive("24.20.0");
        string primaryError = $"success result denied for '{RequestPath}' OPENAI_API_KEY=success-secret";
        string publicationError =
            $"failure result denied for '{RequestPath}.result.json' GATEWAY_TOKEN=write-secret";
        string? successResult = null;
        string? failureResult = null;
        int writes = 0;
        var errors = new StringWriter();
        File.WriteAllText(
            RequestPath,
            SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
            {
                RequestId = requestId,
                ArchivePath = archivePath,
                ApplicationDirectory = ApplicationDirectory,
                UpdateUserPath = false
            }));

        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            File.ReadAllText,
            (_, contents) =>
            {
                writes++;
                if (writes == 1)
                {
                    successResult = contents;
                    throw new IOException(primaryError);
                }

                failureResult = contents;
                if (failureWriteDenied)
                {
                    throw new UnauthorizedAccessException(publicationError);
                }
                File.WriteAllText(SessionLaunchProtocol.ResultPathFor(RequestPath), contents);
            },
            () => _root,
            getRuntimeVersion: ReadFixtureVersion,
            stageNativeModules: (_, _) => null,
            errorOutput: errors);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.Equal(2, writes);
        Assert.Equal(requestId, SessionRuntimeProtocol.ReadResult(
            Assert.IsType<string>(successResult)).RequestId);
        SessionRuntimeInstallResult result = SessionRuntimeProtocol.ReadResult(
            Assert.IsType<string>(failureResult));
        Assert.Equal(requestId, result.RequestId);
        Assert.Equal(primaryError, result.Error);

        string output = errors.ToString();
        if (!failureWriteDenied)
        {
            Assert.Equal(result, SessionRuntimeProtocol.ReadResult(
                File.ReadAllText(SessionLaunchProtocol.ResultPathFor(RequestPath))));
            Assert.Empty(output);
            return;
        }
        Assert.Contains("success result denied", output, StringComparison.Ordinal);
        Assert.Contains("failure result denied", output, StringComparison.Ordinal);
        Assert.Contains(RequestPath, output, StringComparison.Ordinal);
        Assert.Contains("<redacted>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("success-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("write-secret", output, StringComparison.Ordinal);
        _ = Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.True(output.Length < 1200);
    }

    [Theory]
    [InlineData("""{"token":"json-secret"}""", "json-secret")]
    [InlineData("https://example.invalid/#token=url-secret", "url-secret")]
    [InlineData("OPENAI_API_KEY=environment-secret", "environment-secret")]
    [InlineData("Authorization: Bearer header-secret", "header-secret")]
    [InlineData("operation failed\r\n\tprivate detail", "\r")]
    public void FailureFallbackRedactsCredentialsAndStaysOnOneLine(string detail, string excluded)
    {
        var errors = new StringWriter();
        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            _ => throw new IOException(detail),
            (_, _) => throw new IOException(detail),
            errorOutput: errors);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        string output = errors.ToString();
        Assert.DoesNotContain(excluded, output.TrimEnd('\r', '\n'), StringComparison.Ordinal);
        _ = Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void OversizedFailureDetailDoesNotExposeATruncatedSecret()
    {
        string detail = "{\"token\":\"" + new string('s', 5000) + "\"}";
        var errors = new StringWriter();
        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            _ => throw new IOException(detail),
            (_, _) => throw new IOException("result publication denied"),
            errorOutput: errors);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.Contains("error detail omitted (too long)", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("result publication denied", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('s', 10), errors.ToString(), StringComparison.Ordinal);
        Assert.True(errors.ToString().Length < 1200);
    }

    [Fact]
    public void ABrokenStderrDoesNotReplaceTheHelperFailureExitCode()
    {
        int exitCode = SessionRuntimeInstaller.Run(
            RequestPath,
            _ => throw new IOException("operation failed"),
            (_, _) => throw new IOException("result publication denied"),
            errorOutput: new FailingWriter());

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
    }

    private sealed class FailingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("stderr unavailable");
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
