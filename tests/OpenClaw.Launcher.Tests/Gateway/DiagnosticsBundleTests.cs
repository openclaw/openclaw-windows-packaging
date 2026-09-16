using System.IO.Compression;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class DiagnosticsBundleTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public async Task CollectionPreservesHostLogsAndRedactsEveryTextSource()
    {
        (GatewayRuntime runtime, HostPaths paths) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(
            paths.LogPath,
            """gateway failed with {"apiKey":"do-not-share"}""");
        string bundlePath = Path.Combine(_root, "diagnostics.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
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
        (GatewayRuntime runtime, HostPaths paths) = CreateRuntime();
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
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "host/openclaw.log");
    }

    [Fact]
    public async Task CollectionIncludesThePreResetDiagnosticReport()
    {
        (GatewayRuntime runtime, HostPaths paths) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PreResetReportPath)!);
        await File.WriteAllTextAsync(paths.PreResetReportPath, "sessionSandboxId=fixture");
        string bundlePath = Path.Combine(_root, "pre-reset.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
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
        (GatewayRuntime runtime, HostPaths paths) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(paths.LogPath, "host evidence");
        await File.WriteAllTextAsync(paths.SessionStatePath, "{not-json");
        string bundlePath = Path.Combine(_root, "host-only.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        Assert.False(result.SessionReached);
        Assert.Contains(
            result.Notes,
            note => note.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase));
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "host/openclaw.log");
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
        (GatewayRuntime runtime, HostPaths paths) = CreateRuntime();
        string bundlePath = Path.Combine(_root, "existing.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(paths.LogPath, "host evidence");
        await File.WriteAllTextAsync(bundlePath, "existing");

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => runtime.CollectLogsAsync(bundlePath, CancellationToken.None));

        Assert.Contains(bundlePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("--output", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (GatewayRuntime Runtime, HostPaths Paths) CreateRuntime()
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
        return (runtime, paths);
    }
}
