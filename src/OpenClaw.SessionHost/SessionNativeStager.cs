using System.Security.Cryptography;
using OpenClaw.SessionProtocol;

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
    internal static readonly string[] NativeExtensions = [".node", ".dll", ".exe"];

    /// <summary>
    /// Mirrors every native-bearing package and returns the staged root, or
    /// <see langword="null"/> when the application carries no native code.
    /// </summary>
    /// <remarks>
    /// An existing root is reused only after its whole file set verifies
    /// against the installed application. Its completion marker says a copy
    /// once finished, not that the files are still the ones the package needs.
    /// </remarks>
    public static string? Stage(string applicationDirectory, string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);

        NativeStageInventory? expected = NativeStageInventory.Load(applicationDirectory);
        if (expected is null)
        {
            return null;
        }

        string parent = Path.Combine(
            localApplicationData,
            "OpenClawGatewayMSIX",
            DirectoryName);
        string root = Path.Combine(parent, expected.ContentId);

        using (FileStream? lease = OpenConsumerLease(root))
        {
            if (lease is not null && Verify(root, expected).State == NativeStageState.Current)
            {
                // Reclamation runs here too. An upgrade that could not take a
                // root back because it still had a consumer must get another
                // attempt once that consumer is gone, and after the upgrade
                // every later setup sees unchanged content and returns here.
                RemoveSupersededRoots(parent, expected.ContentId);
                return root;
            }
        }

        string staging = $"{root}.stage-{Guid.NewGuid():N}";
        try
        {
            CopyVerified(expected, Path.Combine(staging, ModulesDirectoryName));

            // Written last: a marker only ever appears over a complete copy, so
            // an interrupted staging is redone rather than trusted.
            File.WriteAllText(Path.Combine(staging, MarkerFileName), expected.ContentId);

            // An existing root here failed verification, so it is stale,
            // incomplete, or altered. Renamed rather than deleted in place, for
            // the same reason superseded roots are.
            if (Directory.Exists(root))
            {
                Directory.Move(
                    root,
                    Path.Combine(parent, $"{DiscardedPrefix}{Guid.NewGuid():N}"));
            }

            Directory.CreateDirectory(parent);
            Directory.Move(staging, root);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        RemoveSupersededRoots(parent, expected.ContentId);
        return root;
    }

    /// <summary>
    /// Admits a launch against the recorded stage, returning the lease the
    /// caller must hold until the launched process tree has ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lease is taken before the files are verified and is never released
    /// between verification and launch, so setup cannot reclaim the root in
    /// between. Verification compares the complete file set, lengths, and
    /// bytes with the installed application's inventory: a marker or a
    /// matching directory name alone never admits a launch.
    /// </para>
    /// <para>
    /// <see langword="null"/> is returned only when the application carries no
    /// native dependencies and setup recorded none. Every other mismatch
    /// fails with <see cref="SessionLaunchProtocol.NativeStageStaleErrorCode"/>
    /// before anything starts, because a launch that mixes the installed
    /// application with another stage fails later and far from the cause.
    /// </para>
    /// </remarks>
    public static FileStream? Admit(string applicationDirectory, string? rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        NativeStageInventory? expected = NativeStageInventory.Load(applicationDirectory);
        if (expected is null)
        {
            return rootPath is null
                ? null
                : throw Stale(new NativeStageCheck(NativeStageState.StaleSource, null, null));
        }

        if (rootPath is null)
        {
            throw Stale(new NativeStageCheck(NativeStageState.Missing, expected, null));
        }

        FileStream lease = OpenConsumerLease(rootPath)
            ?? throw Stale(new NativeStageCheck(NativeStageState.Missing, expected, null));
        try
        {
            NativeStageCheck check = Verify(rootPath, expected);
            return check.State == NativeStageState.Current ? lease : throw Stale(check);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static SessionLaunchException Stale(NativeStageCheck check) =>
        new(
            $"The isolated session's staged native dependencies {check.Describe()}, so " +
            "nothing was started. Run `clawctl setup` to refresh them.",
            SessionLaunchProtocol.NativeStageStaleErrorCode);

    /// <summary>
    /// Compares a staged root with the files the installed application needs.
    /// </summary>
    /// <remarks>
    /// The caller holds the root's lease. Reparse points are refused outright:
    /// a staged package must be the agent's own copy, not a link to content
    /// whose bytes could change behind the verification.
    /// </remarks>
    internal static NativeStageCheck Verify(string root, NativeStageInventory expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(expected);

        string? observedContentId = null;
        try
        {
            observedContentId = File.ReadAllText(Path.Combine(root, MarkerFileName)).Trim();
            if (IsReparsePoint(root))
            {
                return new NativeStageCheck(
                    NativeStageState.UnsupportedLayout, expected, observedContentId);
            }

            Dictionary<string, string> staged = new(StringComparer.OrdinalIgnoreCase);
            string modules = Path.Combine(root, ModulesDirectoryName);
            if (Directory.Exists(modules) &&
                (IsReparsePoint(modules) || !CollectFiles(modules, modules, staged)))
            {
                return new NativeStageCheck(
                    NativeStageState.UnsupportedLayout, expected, observedContentId);
            }

            int verified = 0;
            int missing = 0;
            int mismatched = 0;
            foreach (NativeStageFile file in expected.Files)
            {
                if (!staged.Remove(file.RelativePath, out string? path))
                {
                    missing++;
                    continue;
                }

                using FileStream stream = new(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length == file.Length &&
                    CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), file.Sha256))
                {
                    verified++;
                }
                else
                {
                    mismatched++;
                }
            }

            int extra = staged.Count;
            NativeStageState state =
                missing == 0 && extra == 0 && mismatched == 0
                    ? NativeStageState.Current
                    : !string.Equals(observedContentId, expected.ContentId, StringComparison.Ordinal)
                        ? NativeStageState.StaleSource
                        : missing > 0 || extra > 0
                            ? NativeStageState.Incomplete
                            : NativeStageState.ContentMismatch;
            return new NativeStageCheck(state, expected, observedContentId)
            {
                VerifiedFileCount = verified,
                MissingFileCount = missing,
                ExtraFileCount = extra,
                MismatchedFileCount = mismatched
            };
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new NativeStageCheck(NativeStageState.Missing, expected, observedContentId);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new NativeStageCheck(NativeStageState.Inaccessible, expected, observedContentId);
        }
    }

    // Returns false on a reparse point anywhere below the staged node_modules.
    private static bool CollectFiles(
        string modules,
        string directory,
        Dictionary<string, string> files)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos(
            "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            if (entry is DirectoryInfo child)
            {
                if (!CollectFiles(modules, child.FullName, files))
                {
                    return false;
                }
            }
            else
            {
                files[Path.GetRelativePath(modules, entry.FullName)] = entry.FullName;
            }
        }

        return true;
    }

    private static bool IsReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// Copies exactly the inventoried files, checking each copy's length and
    /// bytes as it is written.
    /// </summary>
    /// <remarks>
    /// Streams rather than calls <see cref="File.Copy(string, string)"/>, so
    /// package EFS encryption is not carried into the agent profile.
    /// </remarks>
    private static void CopyVerified(NativeStageInventory expected, string destination)
    {
        byte[] buffer = new byte[81920];
        foreach (NativeStageFile file in expected.Files)
        {
            string target = Path.Combine(destination, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            using (FileStream input = File.OpenRead(Path.Combine(expected.ModulesDirectory, file.RelativePath)))
            using (FileStream output = File.Create(target))
            {
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, count);
                    output.Write(buffer, 0, count);
                    length += count;
                }
            }

            if (length != file.Length ||
                !CryptographicOperations.FixedTimeEquals(hash.GetCurrentHash(), file.Sha256))
            {
                throw new SessionLaunchException(
                    $"The installed native dependency '{file.RelativePath}' does not match " +
                    "the package's payload inventory, so it was not staged. Reinstall the package.");
            }
        }
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
    /// A missing root or marker returns <see langword="null"/>. A launch that
    /// requires the stage goes through <see cref="Admit"/>, which refuses that
    /// case; this bare lease only extends protection to a waiter that outlives
    /// an admitted launch.
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
            if (NativeStageInventory.IsNative(file))
            {
                yield return file;
            }
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
