using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.StateTransfer;

internal sealed class ArchiveFixtureApplication : IStateArchiveApplication
{
    public IReadOnlyList<string> AdditionalDependencies { get; set; } = [];
    public List<string> CapturedProfiles { get; } = [];
    public byte[]? CapturedWal { get; private set; }
    public Action<string>? BeforeCapture { get; set; }
    public IReadOnlyList<StateTransferAsset>? ExtractionAssets { get; set; }

    public StateArchiveApplicationResult Capture(string profile, string output, bool dryRun)
    {
        BeforeCapture?.Invoke(profile);
        CapturedProfiles.Add(profile);
        List<StateTransferAsset> assets = [
            new("state", Path.Combine(profile, ".openclaw"), @"snapshot/payload/state")
        ];
        if (Workspace(profile) is { } workspace &&
            !workspace.StartsWith(Path.Combine(profile, ".openclaw") + "\\", StringComparison.OrdinalIgnoreCase))
        {
            assets.Add(new StateTransferAsset("workspace", workspace, @"snapshot/payload/workspace"));
        }
        if (!dryRun)
        {
            WriteArchive(profile, assets, output);
        }
        return new StateArchiveApplicationResult(output, assets, []);
    }

    public StateArchiveApplicationResult Extract(string profile, string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        using (FileStream file = File.OpenRead(archive))
        using (var compressed = new GZipStream(file, CompressionMode.Decompress))
        {
            TarFile.ExtractToDirectory(compressed, destination, overwriteFiles: false);
        }
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(destination, "snapshot", "manifest.json")));
        return new StateArchiveApplicationResult(archive,
            ExtractionAssets ?? [.. manifest.RootElement.GetProperty("assets").EnumerateArray().Select(asset => new StateTransferAsset(
                asset.GetProperty("kind").GetString()!,
                asset.GetProperty("sourcePath").GetString()!,
                asset.GetProperty("archivePath").GetString()!))],
            [],
            manifest.RootElement.GetProperty("profile").GetString());
    }

    public IReadOnlyList<string> RequiredSources(string profile, string previousProfile) =>
        Workspace(profile) is { } workspace
            ? [workspace, .. AdditionalDependencies]
            : AdditionalDependencies;

    public void RebaseConfiguration(string profile, string previousProfile, string currentProfile)
    {
        string path = Path.Combine(profile, ".openclaw", "openclaw.json");
        if (!File.Exists(path))
        {
            return;
        }
        JsonNode configuration = JsonNode.Parse(File.ReadAllText(path))!;
        if (configuration["agents"]?["defaults"]?["workspace"] is JsonNode workspace)
        {
            string relative = Path.GetRelativePath(previousProfile, workspace.GetValue<string>());
            configuration["agents"]!["defaults"]!["workspace"] = Path.Combine(currentProfile, relative);
        }
        File.WriteAllText(path, configuration.ToJsonString());
    }

    private void WriteArchive(string profile, List<StateTransferAsset> assets, string output)
    {
        string wal = Path.Combine(profile, ".openclaw", "state.sqlite-wal");
        if (File.Exists(wal))
        {
            CapturedWal = File.ReadAllBytes(wal);
        }
        using FileStream file = new(output, FileMode.CreateNew, FileAccess.Write);
        using var compressed = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new TarWriter(compressed, leaveOpen: true);
        WriteBytes(writer, "snapshot/manifest.json", Manifest(profile, assets));
        foreach (StateTransferAsset asset in assets)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, asset.ArchivePath));
            foreach (string directory in Directory.GetDirectories(asset.SourcePath, "*", SearchOption.AllDirectories))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory,
                    asset.ArchivePath + "/" + Path.GetRelativePath(asset.SourcePath, directory).Replace('\\', '/')));
            }
            foreach (string source in Directory.GetFiles(asset.SourcePath, "*", SearchOption.AllDirectories))
            {
                WriteFile(writer, source,
                    asset.ArchivePath + "/" + Path.GetRelativePath(asset.SourcePath, source).Replace('\\', '/'));
            }
        }
    }

    private static byte[] Manifest(string profile, List<StateTransferAsset> assets)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("profile", profile);
            json.WriteStartArray("assets");
            foreach (StateTransferAsset asset in assets)
            {
                json.WriteStartObject();
                json.WriteString("kind", asset.Kind);
                json.WriteString("sourcePath", asset.SourcePath);
                json.WriteString("archivePath", asset.ArchivePath);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteFile(TarWriter writer, string source, string destination)
    {
        using FileStream input = File.OpenRead(source);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, destination) { DataStream = input });
    }

    private static void WriteBytes(TarWriter writer, string destination, byte[] data)
    {
        using var input = new MemoryStream(data);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, destination) { DataStream = input });
    }

    private static string? Workspace(string profile)
    {
        string path = Path.Combine(profile, ".openclaw", "openclaw.json");
        return File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))?["agents"]?["defaults"]?["workspace"]?.GetValue<string>()
            : null;
    }
}
