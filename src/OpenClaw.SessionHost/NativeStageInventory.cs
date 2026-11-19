using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>Where an expected native stage inventory came from.</summary>
internal enum NativeStageProvenance
{
    /// <summary>The installed package's own <c>payload-files.json</c>.</summary>
    PackageInventory,

    /// <summary>
    /// Hashed from an application directory that carries no package inventory,
    /// such as a contributor layout or a test fixture.
    /// </summary>
    UnpackagedSource,
}

/// <summary>One file a complete native stage must contain.</summary>
/// <param name="Package">The owning package, relative to <c>node_modules</c>.</param>
/// <param name="RelativePath">The file, relative to <c>node_modules</c>.</param>
/// <param name="Length">The file's length in bytes.</param>
/// <param name="Sha256">The file's SHA-256 digest.</param>
internal sealed record NativeStageFile(
    string Package,
    string RelativePath,
    long Length,
    byte[] Sha256);

/// <summary>
/// The exact file set the installed application's native stage must hold.
/// </summary>
/// <remarks>
/// <para>
/// The installed package carries <c>payload/payload-files.json</c>, the
/// path, length, and SHA-256 of every application file, beside its
/// <c>app</c> directory. Package content is integrity-protected, so the
/// inventory both selects the native-bearing packages and states their
/// expected bytes without rereading tens of thousands of package files on
/// every launch.
/// </para>
/// <para>
/// A layout without that inventory falls back to scanning and hashing the
/// source packages, as staging always has, and says so through
/// <see cref="Provenance"/> rather than presenting the result as package
/// inventory.
/// </para>
/// </remarks>
internal sealed class NativeStageInventory
{
    private const string ModulesDirectoryName = "node_modules";
    private const string ModulesPrefix = ModulesDirectoryName + "/";

    private NativeStageInventory(
        NativeStageProvenance provenance,
        string modulesDirectory,
        IReadOnlyList<string> packages,
        IReadOnlyList<NativeStageFile> files)
    {
        Provenance = provenance;
        ModulesDirectory = modulesDirectory;
        Packages = packages;
        Files = files;
        ContentId = ComputeContentId(packages, files);
    }

    public NativeStageProvenance Provenance { get; }

    /// <summary>The installed application's own <c>node_modules</c>.</summary>
    public string ModulesDirectory { get; }

    /// <summary>The native-bearing packages, ordered as staging orders them.</summary>
    public IReadOnlyList<string> Packages { get; }

    public IReadOnlyList<NativeStageFile> Files { get; }

    /// <summary>
    /// Identifies the staged content, computed exactly as earlier releases
    /// computed it so their stage directories remain recognizable.
    /// </summary>
    public string ContentId { get; }

    /// <summary>
    /// The inventory that sits beside an installed application directory.
    /// </summary>
    internal static string ResolveManifestPath(string applicationDirectory)
    {
        string application = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(applicationDirectory));
        string packageRoot = Path.GetDirectoryName(application)
            ?? throw new SessionLaunchException(
                "The installed application directory has no package root.");
        return Path.Combine(packageRoot, "payload", "payload-files.json");
    }

    /// <summary>
    /// Returns the expected stage, or <see langword="null"/> when the
    /// application carries no native artifacts.
    /// </summary>
    public static NativeStageInventory? Load(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        string modules = Path.Combine(Path.GetFullPath(applicationDirectory), ModulesDirectoryName);
        string manifest = ResolveManifestPath(applicationDirectory);
        if (File.Exists(manifest))
        {
            return FromManifest(manifest, modules);
        }

        return Directory.Exists(modules) ? FromSource(modules) : null;
    }

    private static NativeStageInventory? FromManifest(string manifestPath, string modules)
    {
        List<NativeStageFile> entries = [];
        SortedSet<string> packages = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            byte[] bytes = File.ReadAllBytes(manifestPath);
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith("\xEF\xBB\xBF"u8))
            {
                json = json[3..];
            }

            var reader = new Utf8JsonReader(json);
            Require(reader.Read() && reader.TokenType == JsonTokenType.StartObject);
            bool sawFiles = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals("files"u8))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                sawFiles = true;
                Require(reader.Read() && reader.TokenType == JsonTokenType.StartArray);
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    if (ReadEntry(ref reader) is { } entry)
                    {
                        entries.Add(entry);
                        if (IsNative(entry.RelativePath))
                        {
                            packages.Add(entry.Package);
                        }
                    }
                }

                Require(reader.TokenType == JsonTokenType.EndArray);
            }

            Require(sawFiles && reader.TokenType == JsonTokenType.EndObject);
        }
        catch (JsonException exception)
        {
            throw InvalidInventory(exception);
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidInventory(exception);
        }

        if (packages.Count == 0)
        {
            return null;
        }

        List<NativeStageFile> selected = [.. entries.Where(entry => packages.Contains(entry.Package))];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (NativeStageFile entry in selected)
        {
            if (!seen.Add(entry.RelativePath))
            {
                throw InvalidInventory(null);
            }
        }

        return new NativeStageInventory(
            NativeStageProvenance.PackageInventory,
            modules,
            [.. packages],
            Order(selected));
    }

    private static NativeStageFile? ReadEntry(ref Utf8JsonReader reader)
    {
        string? path = null;
        long? length = null;
        string? sha256 = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("path"u8))
            {
                Require(reader.Read() && reader.TokenType == JsonTokenType.String);
                path = reader.GetString();
            }
            else if (reader.ValueTextEquals("length"u8))
            {
                Require(reader.Read() && reader.TokenType == JsonTokenType.Number &&
                    reader.TryGetInt64(out long value) && value >= 0);
                length = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("sha256"u8))
            {
                Require(reader.Read() && reader.TokenType == JsonTokenType.String);
                sha256 = reader.GetString();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        Require(reader.TokenType == JsonTokenType.EndObject &&
            IsSafeRelativePath(path) && length is not null &&
            sha256 is { Length: 64 } && sha256.All(char.IsAsciiHexDigit));

        // Only the application's own node_modules can carry a staged package.
        if (!path!.StartsWith(ModulesPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string relative = path[ModulesPrefix.Length..].Replace('/', '\\');
        return SessionNativeStager.TryGetOwningPackage(relative, out string package)
            ? new NativeStageFile(package, relative, length!.Value, Convert.FromHexString(sha256!))
            : null;
    }

    private static NativeStageInventory? FromSource(string modules)
    {
        IReadOnlyList<string> packages = SessionNativeStager.DiscoverNativePackages(modules);
        if (packages.Count == 0)
        {
            return null;
        }

        List<NativeStageFile> files = [];
        foreach (string package in packages)
        {
            foreach (string file in Directory.EnumerateFiles(
                Path.Combine(modules, package), "*", SearchOption.AllDirectories))
            {
                using FileStream stream = File.OpenRead(file);
                files.Add(new NativeStageFile(
                    package,
                    Path.GetRelativePath(modules, file),
                    stream.Length,
                    SHA256.HashData(stream)));
            }
        }

        return new NativeStageInventory(
            NativeStageProvenance.UnpackagedSource,
            modules,
            packages,
            Order(files));
    }

    private static List<NativeStageFile> Order(IEnumerable<NativeStageFile> files) =>
        [.. files
            .OrderBy(file => file.Package, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Hashes each staged file's path and bytes, package by package.
    /// </summary>
    /// <remarks>
    /// Size and write time are not sufficient: an npm package republished at
    /// the same length keeps the timestamps its tarball carries, and a
    /// Developer Mode layout is rewritten in place, so either could otherwise
    /// present changed executable content under an identifier already on disk.
    /// </remarks>
    private static string ComputeContentId(
        IReadOnlyList<string> packages,
        IReadOnlyList<NativeStageFile> files)
    {
        using IncrementalHash content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string package in packages)
        {
            content.AppendData(Encoding.UTF8.GetBytes($"{package}\n"));
            foreach (NativeStageFile file in files.Where(file =>
                string.Equals(file.Package, package, StringComparison.OrdinalIgnoreCase)))
            {
                content.AppendData(Encoding.UTF8.GetBytes($"{file.RelativePath}|"));
                content.AppendData(file.Sha256);
                content.AppendData("\n"u8);
            }
        }

        return Convert.ToHexStringLower(content.GetCurrentHash())[..32];
    }

    internal static bool IsNative(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        return SessionNativeStager.NativeExtensions.Any(candidate =>
            string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith('/') &&
        !path.Contains('\\', StringComparison.Ordinal) &&
        !path.Contains(':', StringComparison.Ordinal) &&
        !Path.IsPathRooted(path) &&
        !path.Split('/').Any(static segment => segment is "" or "." or "..");

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("The payload inventory is malformed.");
        }
    }

    private static SessionLaunchException InvalidInventory(Exception? cause) =>
        cause is null
            ? new SessionLaunchException(
                "The installed package's payload inventory is invalid, so its native " +
                "dependencies cannot be verified. Reinstall the package.")
            : new SessionLaunchException(
                "The installed package's payload inventory is invalid, so its native " +
                "dependencies cannot be verified. Reinstall the package.",
                cause);
}
