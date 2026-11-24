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
    private const string StagingPrefix = ".stage-";

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
    public static string? Stage(
        string applicationDirectory,
        string localApplicationData,
        string? currentRootPath = null) =>
        Publish(applicationDirectory, localApplicationData, currentRootPath).RootPath;

    /// <summary>
    /// Selects a verified stage for the installed application, publishing a
    /// new generation when no existing one verifies, then reclaims every
    /// other stage that no running process still holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root setup recorded, and the content-named root earlier releases
    /// used, are reused only after their whole file set verifies against the
    /// installed application. A completion marker says a copy once finished,
    /// not that the files are still the ones the package needs.
    /// </para>
    /// <para>
    /// A replacement is always a new directory. A stage that failed
    /// verification may still be the one a running gateway or idle agent
    /// shell resolves through, so it is never overwritten or forced aside; it
    /// is reclaimed only once nothing holds it. A failure before publication
    /// leaves every existing stage exactly as it was.
    /// </para>
    /// <para>
    /// Setup runs this under the host's lifecycle lock, so one publication
    /// runs at a time for an agent.
    /// </para>
    /// </remarks>
    public static NativeStagePublication Publish(
        string applicationDirectory,
        string localApplicationData,
        string? currentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);

        string parent = Path.GetFullPath(Path.Combine(
            localApplicationData,
            "OpenClawGatewayMSIX",
            DirectoryName));
        NativeStageInventory? expected = NativeStageInventory.Load(applicationDirectory);
        string? selected = null;
        bool reused = false;
        if (expected is not null)
        {
            foreach (string candidate in Candidates(parent, expected.ContentId, currentRootPath))
            {
                using FileStream? lease = OpenConsumerLease(candidate);
                if (lease is not null && Verify(candidate, expected).State == NativeStageState.Current)
                {
                    selected = candidate;
                    reused = true;
                    break;
                }
            }

            selected ??= PublishGeneration(parent, expected);
        }

        // Runs on reuse too. An upgrade that could not take a root back
        // because it still had a consumer gets another attempt at every later
        // setup, once that consumer is gone.
        int retained = Directory.Exists(parent) ? ReclaimSuperseded(parent, selected) : 0;
        return new NativeStagePublication(selected, reused, retained);
    }

    // Only direct children of this agent's stage directory are candidates, so
    // a recorded path can never select or verify content anywhere else.
    private static IEnumerable<string> Candidates(
        string parent,
        string contentId,
        string? currentRootPath)
    {
        if (currentRootPath is not null &&
            Path.GetDirectoryName(Path.GetFullPath(currentRootPath)) is { } directory &&
            string.Equals(directory, parent, StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(currentRootPath).StartsWith('.'))
        {
            yield return Path.GetFullPath(currentRootPath);
        }

        string legacy = Path.Combine(parent, contentId);
        if (!string.Equals(legacy, currentRootPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return legacy;
        }
    }

    private static string PublishGeneration(string parent, NativeStageInventory expected)
    {
        Directory.CreateDirectory(parent);
        string generation = Path.Combine(parent, $"{Guid.NewGuid():N}");
        string staging = Path.Combine(parent, $"{StagingPrefix}{Guid.NewGuid():N}");
        try
        {
            CopyVerified(expected, Path.Combine(staging, ModulesDirectoryName));

            // Written last and flushed: a marker only ever appears over a
            // complete, verified copy, so an interrupted staging is redone
            // rather than trusted.
            using (FileStream marker = new(
                Path.Combine(staging, MarkerFileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                marker.Write(System.Text.Encoding.UTF8.GetBytes(expected.ContentId));
                marker.Flush(flushToDisk: true);
            }

            Directory.Move(staging, generation);
            return generation;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
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
    /// The marker is opened with read sharing only, so it conflicts with the
    /// exclusive gate <see cref="TryReclaim"/> takes, and Windows also refuses
    /// to rename the root while it is open. The caller keeps the handle for
    /// the launched process's lifetime.
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
    /// Reclaims every stage except the selected one that no running process
    /// still holds, returning how many were left in place.
    /// </summary>
    private static int ReclaimSuperseded(string parent, string? selected)
    {
        int retained = 0;
        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            if (!string.Equals(directory, selected, StringComparison.OrdinalIgnoreCase) &&
                !TryReclaim(directory))
            {
                retained++;
            }
        }

        return retained;
    }

    /// <summary>
    /// Takes one superseded stage back if nothing holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setup reuses a running session, so a superseded root can still be the
    /// one a gateway, a foreground launch, or an idle agent shell resolves
    /// native addons through. Reclaiming such a root would leave that process
    /// resolving back to the packaged copies it cannot load.
    /// </para>
    /// <para>
    /// Every consumer holds the marker open with read sharing only, for its
    /// whole lifetime. The reclaimer opens the same marker without read
    /// sharing, which conflicts with every consumer lease in both directions:
    /// it fails while any consumer holds the root, and while it is held no new
    /// lease can open. Under that gate the marker is deleted, so the root can
    /// never be admitted again. Success of a rename or delete is never taken
    /// as proof that no process uses the root.
    /// </para>
    /// <para>
    /// The retired root is then renamed before it is deleted, so a process
    /// that maps a staged file without a lease keeps the whole tree rather
    /// than a partial one; a root that cannot be renamed yet is removed by a
    /// later setup.
    /// </para>
    /// <para>
    /// Reclaiming runs after the replacement is published, so a marker that
    /// cannot be retired, such as a read-only one, leaves the stage whole for
    /// a later setup rather than failing the setup that already succeeded.
    /// </para>
    /// </remarks>
    internal static bool TryReclaim(string directory)
    {
        string marker = Path.Combine(directory, MarkerFileName);
        if (File.Exists(marker))
        {
            using FileStream? gate = TryOpenReclaimGate(directory);
            if (gate is null)
            {
                // Held by a consumer. Left intact for a later setup.
                return false;
            }

            try
            {
                File.Delete(marker);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        string discarded = directory;
        if (!Path.GetFileName(directory).StartsWith(DiscardedPrefix, StringComparison.Ordinal))
        {
            discarded = Path.Combine(
                Path.GetDirectoryName(directory)!,
                $"{DiscardedPrefix}{Guid.NewGuid():N}");
            try
            {
                Directory.Move(directory, discarded);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        try
        {
            Directory.Delete(discarded, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Retired and renamed already, so nothing can resolve into it;
            // the remaining files are removed by a later setup.
        }

        return true;
    }
    /// <summary>
    /// Opens the exclusive reclaim gate on a stage's marker, or returns
    /// <see langword="null"/> while any consumer holds it.
    /// </summary>
    /// <remarks>
    /// Shares delete only, so it conflicts with every consumer lease in both
    /// directions while still letting its holder retire the marker.
    /// </remarks>
    internal static FileStream? TryOpenReclaimGate(string directory)
    {
        try
        {
            return new FileStream(
                Path.Combine(directory, MarkerFileName),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Delete);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What one setup's native staging selected and left behind.</summary>
/// <param name="RootPath">The verified stage, or <see langword="null"/> when the application carries no native code.</param>
/// <param name="Reused">Whether an existing stage verified, so nothing was copied.</param>
/// <param name="RetainedRootCount">Superseded stages left in place because a process still holds them.</param>
internal sealed record NativeStagePublication(
    string? RootPath,
    bool Reused,
    int RetainedRootCount);
