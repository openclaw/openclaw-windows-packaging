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
