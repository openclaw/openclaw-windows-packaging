using System.IO.Compression;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class DiagnosticsBundleTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public async Task CollectionPreservesHostLogsAndRedactsEveryTextSource()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(
            paths.LogPath,
            """gateway failed with {"apiKey":"do-not-share"}""");
        string bundlePath = Path.Combine(_root, "diagnostics.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            candidate => candidate.FullName == "host/openclaw.log");
        using var reader = new StreamReader(entry.Open());
        string text = await reader.ReadToEndAsync();
        Assert.Contains(DiagnosticsRedactor.Placeholder, text, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-share", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionReadsALiveLogWithWriteAndDeleteSharing()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        using FileStream liveLog = new(
            paths.LogPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        using (var writer = new StreamWriter(
            liveLog,
            leaveOpen: true))
        {
            writer.Write("still running");
            writer.Flush();
        }

        string bundlePath = Path.Combine(_root, "live.zip");
        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "host/openclaw.log");
    }

    [Fact]
    public async Task CollectionIncludesThePreResetDiagnosticReport()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PreResetReportPath)!);
        await File.WriteAllTextAsync(paths.PreResetReportPath, "sessionSandboxId=fixture");
        string bundlePath = Path.Combine(_root, "pre-reset.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            candidate => candidate.FullName == "host/pre-reset.log");
        using var reader = new StreamReader(entry.Open());
        Assert.Contains(
            "sessionSandboxId=fixture",
            await reader.ReadToEndAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptSessionStateStillProducesAHostOnlyBundle()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(paths.LogPath, "host evidence");
        await File.WriteAllTextAsync(paths.SessionStatePath, "{not-json");
        string bundlePath = Path.Combine(_root, "host-only.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        Assert.False(result.SessionReached);
        Assert.Contains(
            result.Notes,
            note => note.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase));
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "host/openclaw.log");
    }

    // A session that was provisioned but never started has a recorded
    // workspace nothing created. The note must say so, not merely that the
    // helper is unstaged, and the manifest must name the collecting build.
    [Fact]
    public async Task HostOnlyBundleNamesTheUnderlyingCauseAndTheEnvironment()
    {
        (GatewayRuntime runtime, HostPaths paths, SessionRuntime session) =
            CreateRuntime(workspacePath: Path.Combine(_root, "never-created"));
        await session.Coordinator.EnsureStartedAsync(CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(paths.LogPath, "host evidence");
        string bundlePath = Path.Combine(_root, "unstarted.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            "Windows 10.0.26340.9212 (X64 OS, X64 process); fixture",
            new RecordingProgress(),
            CancellationToken.None);

        Assert.False(result.SessionReached);
        string note = Assert.Single(
            result.Notes,
            candidate => candidate.StartsWith(
                "Agent-side logs could not be collected",
                StringComparison.Ordinal));
        Assert.Contains("has not been staged", note, StringComparison.Ordinal);
        Assert.Contains(
            "---> SessionException: The recorded shared workspace could not be opened safely",
            note,
            StringComparison.Ordinal);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        using var manifest = new StreamReader(
            Assert.Single(archive.Entries, entry => entry.FullName == "manifest.txt").Open());
        string manifestText = await manifest.ReadToEndAsync();
        Assert.Contains(
            "Environment: Windows 10.0.26340.9212 (X64 OS, X64 process); fixture",
            manifestText,
            StringComparison.Ordinal);
        Assert.Contains(note, manifestText, StringComparison.Ordinal);
    }

    [Fact]
    public void FilenameOnlyOutputResolvesAgainstTheWorkingDirectory()
    {
        HostPaths paths = HostPaths.ForRoot(Path.Combine(_root, "state"));

        string resolved = GatewayRuntime.ResolveBundlePath(
            "diagnostics.zip",
            paths,
            _root);

        Assert.Equal(Path.Combine(_root, "diagnostics.zip"), resolved);
    }

    [Fact]
    public void DefaultOutputUsesTheInjectedCollectionTime()
    {
        HostPaths paths = HostPaths.ForRoot(Path.Combine(_root, "state"));

        string resolved = GatewayRuntime.ResolveBundlePath(
            null,
            paths,
            _root,
            new FixedTimeProvider(
                new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero)));

        Assert.Equal(
            Path.Combine(paths.StateRoot, "openclaw-diagnostics-20250102-030405.zip"),
            resolved);
    }

    [Fact]
    public async Task ExplicitExistingOutputNamesThePathAndOutputOption()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        string bundlePath = Path.Combine(_root, "existing.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(paths.LogPath, "host evidence");
        await File.WriteAllTextAsync(bundlePath, "existing");

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => runtime.CollectLogsAsync(bundlePath, environment: null, new RecordingProgress(), CancellationToken.None));

        Assert.Contains(bundlePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("--output", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (GatewayRuntime Runtime, HostPaths Paths, SessionRuntime Session) CreateRuntime(
        string? workspacePath = null)
    {
        HostPaths paths = HostPaths.ForRoot(
            Path.Combine(_root, Guid.NewGuid().ToString("N"), "state"),
            "OpenClaw.Gateway_test");
        string baseDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"), "base");
        string applicationDirectory = Path.Combine(baseDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(Path.Combine(applicationDirectory, "openclaw.mjs"), "fixture");
        string helperPath = SessionRuntime.ResolveHelperPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, "fixture");
        var backend = new FakeMxcSessionClient();
        if (workspacePath is not null)
        {
            backend.Metadata = new MxcProvisionMetadata(
                "agent_1",
                "S-1-5-21-0-0-0-1001",
                workspacePath);
        }

        SessionRuntime session = SessionRuntime.Create(
            paths,
            () => throw new InvalidOperationException("The test supplies its backend."),
            baseDirectory,
            _ => { },
            backend);
        GatewayRuntime runtime = GatewayRuntime.Create(
            new HostOptions(applicationDirectory, null, []),
            paths,
            session,
            _ => { });
        return (runtime, paths, session);
    }
}
