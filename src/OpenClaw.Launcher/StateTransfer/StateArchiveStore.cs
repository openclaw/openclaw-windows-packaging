using System.Security.Cryptography;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.StateTransfer;

internal sealed record StateArchiveEntry(
    string Path,
    long Length,
    DateTimeOffset CreatedUtc,
    bool Protection);

internal sealed class StateArchiveStore
{
    public const string DirectoryName = ".openclaw-backups";
    private const string ProtectionDirectoryName = "before-restore";
    private readonly string _directory;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;

    public StateArchiveStore(
        string directory,
        Action<string> log,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(log);
        _directory = Path.GetFullPath(directory);
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public string DirectoryPath => _directory;

    public string ValidateDestination(string? destination, bool protection = false)
    {
        string path = ResolveDestination(destination, protection);
        TrustedPath.EnsureNoReparsePoints(Path.GetPathRoot(path)!, path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new SessionException($"The archive destination already exists: {path}");
        }
        return path;
    }

    public IReadOnlyList<StateArchiveEntry> List()
    {
        List<StateArchiveEntry> entries = [];
        AddEntries(_directory, protection: false);
        AddEntries(Path.Combine(_directory, ProtectionDirectoryName), protection: true);
        return [.. entries.OrderByDescending(entry => entry.CreatedUtc)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)];

        void AddEntries(string directory, bool protection)
        {
            string[] paths;
            try
            {
                paths = Directory.GetFiles(directory, "*.tar.gz", SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            TrustedPath.EnsureNoReparsePoints(Path.GetPathRoot(directory)!, directory);
            foreach (string path in paths)
            {
                using FileStream file = TrustedPath.OpenRead(directory, path, protectContents: true);
                entries.Add(new StateArchiveEntry(
                    path,
                    file.Length,
                    File.GetLastWriteTimeUtc(path),
                    protection));
            }
        }
    }

    public string Select(string? archive)
    {
        if (!string.IsNullOrWhiteSpace(archive))
        {
            return Path.GetFullPath(archive);
        }
        return List().FirstOrDefault(entry => !entry.Protection)?.Path
            ?? throw new SessionException(
                $"No recovery archive was found in '{_directory}'. " +
                "Run `clawctl backup` or provide an archive to `clawctl restore`.");
    }

    public async Task<string> PublishAsync(
        Stream source,
        StateTransferArchive archive,
        string? destination,
        bool protection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(archive);
        if (!archive.Verified || archive.Length <= 0)
        {
            throw new SessionException("An unverified or empty archive cannot be retained.");
        }

        string path = ValidateDestination(destination, protection);
        string parent = Path.GetDirectoryName(path)!;
        TrustedPath.EnsureNoReparsePoints(Path.GetPathRoot(parent)!, parent);
        Directory.CreateDirectory(parent);
        using TrustedPath.ValidatedDirectory root =
            TrustedPath.TryOpenValidatedDirectory(parent, expectedIdentity: null)
            ?? throw new SessionException($"The archive directory is not a regular directory: {parent}");
        string temporary = Path.Combine(parent, $".archive-{Guid.NewGuid():N}.tmp");
        bool published = false;
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            using (Stream output = TrustedPath.CreateNew(root, temporary))
            {
                byte[] buffer = new byte[64 * 1024];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    length = checked(length + count);
                    if (length > archive.Length)
                    {
                        throw new SessionException("The captured archive changed while it was being retained.");
                    }
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (length != archive.Length ||
                !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), archive.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionException("The captured archive failed its length or SHA-256 check.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
            published = true;
            return path;
        }
        finally
        {
            if (!published)
            {
                try
                {
                    _ = TrustedPath.TryDeleteOwnedEntry(root, temporary, deleteReparsePointLeaf: true);
                }
                catch (Exception exception) when (exception is IOException or SessionException or UnauthorizedAccessException)
                {
                    _log($"Archive staging cleanup failed: {DiagnosticFailure.Describe(exception)}");
                }
            }
        }
    }

    private string ResolveDestination(string? destination, bool protection)
    {
        if (!string.IsNullOrWhiteSpace(destination))
        {
            string path = Path.GetFullPath(destination);
            if (!path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionException("The archive destination must be a .tar.gz file.");
            }
            return path;
        }
        string directory = protection
            ? Path.Combine(_directory, ProtectionDirectoryName)
            : _directory;
        string timestamp = _clock.GetUtcNow().ToString(
            "yyyyMMdd-HHmmss-fffffff",
            System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(directory, $"openclaw-backup-{timestamp}-{Guid.NewGuid():N}.tar.gz");
    }
}
