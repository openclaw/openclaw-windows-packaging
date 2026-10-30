using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.SessionHost;

/// <summary>
/// Mirrors the application's native dependency packages into this account's
/// own profile.
/// </summary>
/// <remarks>
/// <para>
/// The agent identity may read package content but may not map it as an
/// executable image: loading any packaged <c>.node</c> fails with
/// <c>ERR_DLOPEN_FAILED</c> / access denied even though the same bytes load
/// from a writable location. Node.js resolves a native addon through the
/// package directory that owns it, and the packages themselves derive sibling
/// DLLs and helper executables from their own directory, so whole owning
/// package directories are mirrored rather than individual binaries.
/// </para>
/// <para>
/// Only packages that actually carry native artifacts are copied. The rest of
/// the application, which is the overwhelming majority of it, keeps running
/// directly from the immutable package.
/// </para>
/// <para>
/// The set is discovered by scanning, never hard-coded. An upstream revision
/// that introduces a new native dependency is then staged automatically
/// instead of failing at runtime once something happens to call it.
/// </para>
/// <para>
/// The destination is the agent's own profile, not the shared workspace. The
/// agent already controls every process that loads these files, so this moves
/// nothing across a trust boundary; staging into the guest-writable workspace
/// would.
/// </para>
/// </remarks>
internal static class SessionNativeStager
{
    internal const string DirectoryName = "agent-native";

    private const string ModulesDirectoryName = "node_modules";
    private const string MarkerFileName = ".staged-content-id";
    private const string DiscardedPrefix = ".discarded-";

    /// <summary>
    /// File kinds that cannot be loaded from the package by this identity.
    /// </summary>
    /// <remarks>
    /// <c>.node</c> is the addon itself; <c>.dll</c> and <c>.exe</c> cover the
    /// dependent libraries and helper processes an addon starts, which fail the
    /// same way and are located relative to the addon.
    /// </remarks>
    private static readonly string[] NativeExtensions = [".node", ".dll", ".exe"];

    /// <summary>
    /// Mirrors every native-bearing package and returns the staged root, or
    /// <see langword="null"/> when the application carries no native code.
    /// </summary>
    public static string? Stage(string applicationDirectory, string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);

        string modulesDirectory = Path.Combine(applicationDirectory, ModulesDirectoryName);
        if (!Directory.Exists(modulesDirectory))
        {
            return null;
        }

        IReadOnlyList<string> packages = DiscoverNativePackages(modulesDirectory);
        if (packages.Count == 0)
        {
            return null;
        }

        string contentId = ComputeContentId(modulesDirectory, packages);
        string root = Path.Combine(
            localApplicationData,
            "OpenClawGatewayMSIX",
            DirectoryName,
            contentId);

        string marker = Path.Combine(root, MarkerFileName);
        if (File.Exists(marker) &&
            string.Equals(
                File.ReadAllText(marker).Trim(),
                contentId,
                StringComparison.Ordinal))
        {
            // Reclamation runs here too. An upgrade that could not take a root
            // back because it still had a consumer must get another attempt
            // once that consumer is gone, and after the upgrade every later
            // setup sees unchanged content and returns through this path.
            RemoveSupersededRoots(Path.GetDirectoryName(root)!, contentId);
            return root;
        }

        string staging = $"{root}.stage-{Guid.NewGuid():N}";
        try
        {
            foreach (string package in packages)
            {
                CopyDirectory(
                    Path.Combine(modulesDirectory, package),
                    Path.Combine(staging, ModulesDirectoryName, package));
            }

            // Written last: a marker only ever appears over a complete copy, so
            // an interrupted staging is redone rather than trusted.
            File.WriteAllText(Path.Combine(staging, MarkerFileName), contentId);

            // An existing root here failed the marker check, so it is stale or
            // incomplete. Renamed rather than deleted in place, for the same
            // reason superseded roots are.
            if (Directory.Exists(root))
            {
                Directory.Move(
                    root,
                    Path.Combine(
                        Path.GetDirectoryName(root)!,
                        $"{DiscardedPrefix}{Guid.NewGuid():N}"));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            Directory.Move(staging, root);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        RemoveSupersededRoots(Path.GetDirectoryName(root)!, contentId);
        return root;
    }

    /// <summary>
    /// Holds a staged root against reclamation for as long as a process is
    /// resolving native addons through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A consumer is not identified by the files it has mapped. An agent shell
    /// sitting at a prompt has the redirect in its environment and no staged
    /// file open at all, yet every OpenClaw it later runs resolves through that
    /// root. Reclaiming the root underneath it would send those runs back to
    /// the packaged copies this identity cannot load.
    /// </para>
    /// <para>
    /// The marker is opened without delete sharing, which is what makes
    /// Windows refuse to rename the root while the lease is held. The caller
    /// keeps the handle for the launched process's lifetime, so the rename
    /// <see cref="RemoveSupersededRoots"/> attempts answers whether a consumer
    /// is still alive.
    /// </para>
    /// <para>
    /// A missing root or marker returns <see langword="null"/> rather than
    /// failing: there is then nothing to protect, and a launch must never be
    /// blocked by the bookkeeping that protects it.
    /// </para>
    /// </remarks>
    public static FileStream? OpenConsumerLease(string? stagedRootPath)
    {
        if (string.IsNullOrWhiteSpace(stagedRootPath))
        {
            return null;
        }

        try
        {
            return new FileStream(
                Path.Combine(stagedRootPath, MarkerFileName),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the package directories, relative to <c>node_modules</c>, that
    /// contain at least one native artifact.
    /// </summary>
    /// <remarks>
    /// The owning directory is the outermost package under the application's
    /// own <c>node_modules</c>, so a native reached through a nested
    /// <c>node_modules</c> keeps the layout its resolver expects.
    /// </remarks>
    internal static IReadOnlyList<string> DiscoverNativePackages(string modulesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulesDirectory);

        SortedSet<string> packages = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in EnumerateNativeFiles(modulesDirectory))
        {
            string relative = Path.GetRelativePath(modulesDirectory, file);
            if (TryGetOwningPackage(relative, out string? package))
            {
                packages.Add(package);
            }
        }

        return [.. packages];
    }

    /// <summary>
    /// Takes the package name from the front of a path relative to
    /// <c>node_modules</c>, keeping both segments of a scoped name.
    /// </summary>
    internal static bool TryGetOwningPackage(
        string relativePath,
        out string package)
    {
        package = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        string[] segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        if (segments[0].StartsWith('@'))
        {
            if (segments.Length < 3)
            {
                return false;
            }

            package = Path.Combine(segments[0], segments[1]);
            return true;
        }

        package = segments[0];
        return true;
    }

    private static IEnumerable<string> EnumerateNativeFiles(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(
            directory, "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            foreach (string candidate in NativeExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Identifies the staged content so an unchanged package is not recopied
    /// and a changed one cannot be mistaken for it.
    /// </summary>
    /// <remarks>
    /// The identifier covers each staged file's path and its bytes. Size and
    /// write time are not sufficient: an npm package republished at the same
    /// length keeps the timestamps its tarball carries, and a Developer Mode
    /// layout is rewritten in place, so either could otherwise present changed
    /// executable content under an identifier already on disk.
    /// </remarks>
    private static string ComputeContentId(
        string modulesDirectory,
        IReadOnlyList<string> packages)
    {
        using IncrementalHash content =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string package in packages)
        {
            string packageDirectory = Path.Combine(modulesDirectory, package);
            content.AppendData(Encoding.UTF8.GetBytes($"{package}\n"));
            foreach (string file in Directory
                .EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                content.AppendData(
                    Encoding.UTF8.GetBytes(
                        $"{Path.GetRelativePath(modulesDirectory, file)}|"));
                using FileStream stream = File.OpenRead(file);
                content.AppendData(SHA256.HashData(stream));
                content.AppendData("\n"u8);
            }
        }

        return Convert.ToHexStringLower(content.GetCurrentHash())[..32];
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            // Do not carry package EFS encryption into the agent profile.
            using FileStream input = File.OpenRead(file);
            using FileStream output = File.Create(Path.Combine(destination, Path.GetFileName(file)));
            input.CopyTo(output);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>
    /// Drops roots left by earlier package versions, so upgrades do not
    /// accumulate a mirrored copy each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setup reuses a running session, so a superseded root can still be the
    /// one a gateway, a foreground launch, or an idle agent shell resolves
    /// native addons through. Reclaiming such a root would leave that process
    /// resolving back to the packaged copies it cannot load.
    /// </para>
    /// <para>
    /// Every launch that carries the redirect holds <see cref="MarkerFileName"/>
    /// open for its whole lifetime, which denies delete sharing and so makes
    /// Windows refuse to rename the root. Renaming is therefore the lifetime
    /// check, not merely a way to avoid a partial delete: a root that renames
    /// has no live consumer, and one that does not is left whole for a later
    /// setup to reclaim once its consumer ends.
    /// </para>
    /// </remarks>
    private static void RemoveSupersededRoots(string parent, string contentId)
    {
        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            string name = Path.GetFileName(directory);
            if (string.Equals(name, contentId, StringComparison.Ordinal))
            {
                continue;
            }

            string discarded = directory;
            if (!name.StartsWith(DiscardedPrefix, StringComparison.Ordinal))
            {
                discarded = Path.Combine(parent, $"{DiscardedPrefix}{Guid.NewGuid():N}");
                try
                {
                    Directory.Move(directory, discarded);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Still in use. Left intact for a later setup to reclaim.
                    continue;
                }
            }

            try
            {
                Directory.Delete(discarded, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Renamed out of the way already, so no consumer can resolve
                // into it; the remaining files are removed by a later setup.
            }
        }
    }
}
