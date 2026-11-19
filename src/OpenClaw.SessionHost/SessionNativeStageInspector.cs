using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The <c>--inspect-native</c> mode: reports how the recorded native stage
/// compares with the installed application, for <c>clawctl status --native</c>.
/// </summary>
/// <remarks>
/// <para>
/// This uses the same inventory and verification as launch admission, so it
/// reports the state a launch would be refused for. It never stages, repairs,
/// or loads native code, and it holds the root only while reading it.
/// </para>
/// <para>
/// A package's JavaScript and the workers it starts relative to itself
/// resolve wherever the redirect finds them: from the stage when the stage
/// carries the package, otherwise from the immutable package, where this
/// identity cannot load native code. <c>resolvesFrom</c> reports that without
/// executing anything.
/// </para>
/// </remarks>
internal static class SessionNativeStageInspector
{
    private const int MaximumManifestBytes = 64 * 1024;

    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;
        try
        {
            SessionNativeStageInspectRequest request =
                SessionNativeStageProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId;
            SessionNativeStageReport report =
                Inspect(request.ApplicationDirectory!, request.RootPath) with { RequestId = requestId };
            writeFile(resultPath, SessionNativeStageProtocol.SerializeReport(report));
            return 0;
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or UnauthorizedAccessException)
        {
            writeFile(resultPath, SessionNativeStageProtocol.SerializeReport(new SessionNativeStageReport
            {
                RequestId = requestId,
                Error = exception.Message
            }));
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }

    internal static SessionNativeStageReport Inspect(string applicationDirectory, string? rootPath)
    {
        NativeStageInventory? expected = NativeStageInventory.Load(applicationDirectory);
        if (expected is null)
        {
            return new SessionNativeStageReport
            {
                State = rootPath is null
                    ? "not-required"
                    : NativeStageCheck.Name(NativeStageState.StaleSource)
            };
        }

        using FileStream? lease = rootPath is null ? null : SessionNativeStager.OpenConsumerLease(rootPath);
        NativeStageCheck check = lease is null
            ? new NativeStageCheck(NativeStageState.Missing, expected, null)
            : SessionNativeStager.Verify(rootPath!, expected);
        // Per-package resolution is known only when the stage was read, or
        // when there is no stage for the redirect to use. A layout
        // verification rejected, such as a link inside the stage, is not
        // walked again, and a recorded root that could not be leased, such as
        // one whose marker is gone, may still hold files the redirect uses.
        bool layoutTrusted = check.State is not (NativeStageState.UnsupportedLayout or NativeStageState.Inaccessible);
        bool resolutionKnown = rootPath is null || !Directory.Exists(rootPath) || (lease is not null && layoutTrusted);
        string? stagedModules = lease is null || !layoutTrusted ? null : Path.Combine(rootPath!, "node_modules");
        return new SessionNativeStageReport
        {
            State = check.StateName,
            Provenance = expected.Provenance == NativeStageProvenance.PackageInventory
                ? "package-inventory"
                : "unpackaged-source",
            ExpectedContentId = expected.ContentId,
            StagedContentId = Sanitize(check.ObservedContentId, allowPathCharacters: false),
            ExpectedFiles = expected.Files.Count,
            VerifiedFiles = check.VerifiedFileCount,
            MissingFiles = check.State == NativeStageState.Missing ? expected.Files.Count : check.MissingFileCount,
            UnexpectedFiles = check.ExtraFileCount,
            ChangedFiles = check.MismatchedFileCount,
            Packages =
            [
                .. expected.Packages
                    .Take(SessionNativeStageProtocol.MaximumPackages)
                    .Select(package => DescribePackage(expected, package, stagedModules, resolutionKnown))
            ]
        };
    }

    private static SessionNativeStagePackage DescribePackage(
        NativeStageInventory expected,
        string package,
        string? stagedModules,
        bool resolutionKnown)
    {
        List<NativeStageFile> files = [.. expected.Files.Where(file =>
            string.Equals(file.Package, package, StringComparison.OrdinalIgnoreCase))];
        List<(string Path, byte[] Hash)> staged = [];
        int unexpected = 0;
        if (stagedModules is not null)
        {
            foreach (NativeStageFile file in files)
            {
                if (TryHash(Path.Combine(stagedModules, file.RelativePath)) is { } hash)
                {
                    staged.Add((file.RelativePath, hash));
                }
            }

            string packageDirectory = Path.Combine(stagedModules, package);
            if (Directory.Exists(packageDirectory))
            {
                HashSet<string> known = new(
                    files.Select(file => file.RelativePath), StringComparer.OrdinalIgnoreCase);
                unexpected = Directory
                    .EnumerateFiles(
                        packageDirectory,
                        "*",
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            AttributesToSkip = FileAttributes.ReparsePoint
                        })
                    .Count(file => !known.Contains(Path.GetRelativePath(stagedModules, file)));
            }
        }

        string sourceDigest = Digest(files.Select(file => (file.RelativePath, file.Sha256)));
        string? stagedDigest = staged.Count == 0 ? null : Digest(staged);
        return new SessionNativeStagePackage
        {
            Name = Sanitize(package.Replace('\\', '/'), allowPathCharacters: true),
            SourceVersion = ReadVersion(Path.Combine(expected.ModulesDirectory, package, "package.json")),
            StagedVersion = stagedModules is null
                ? null
                : ReadVersion(Path.Combine(stagedModules, package, "package.json")),
            SourceDigest = sourceDigest,
            StagedDigest = stagedDigest,
            FilesMatch = staged.Count == files.Count && unexpected == 0 &&
                string.Equals(sourceDigest, stagedDigest, StringComparison.Ordinal),
            ResolvesFrom = !resolutionKnown
                ? "unknown"
                : staged.Count == files.Count
                    ? "staged"
                    : staged.Count == 0 ? "immutable" : "mixed"
        };
    }

    private static byte[]? TryHash(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return SHA256.HashData(stream);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Digest(IEnumerable<(string Path, byte[] Hash)> files)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string path, byte[] hash) in files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            digest.AppendData(Encoding.UTF8.GetBytes($"{path}|"));
            digest.AppendData(hash);
            digest.AppendData("\n"u8);
        }

        return Convert.ToHexStringLower(digest.GetCurrentHash())[..16];
    }

    // Only the declared version is read, from a bounded file, and it is
    // reported only if it looks like one.
    private static string? ReadVersion(string packageJson)
    {
        try
        {
            using FileStream stream = new(packageJson, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumManifestBytes)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("version", out JsonElement version) &&
                version.ValueKind == JsonValueKind.String
                ? Sanitize(version.GetString(), allowPathCharacters: false)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Sanitize(string? value, bool allowPathCharacters) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+' ||
            (allowPathCharacters && character is '@' or '/'))
            ? value
            : null;
}
