using System.ComponentModel;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.StateTransfer;

internal sealed class RecoverySource : IDisposable
{
    private readonly TrustedPath.ValidatedDirectory _profile;
    private readonly Dictionary<string, FileStream> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrustedPath.ValidatedDirectory> _directories = [];
    private readonly Dictionary<string, string[]> _inventories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _copied = new(StringComparer.OrdinalIgnoreCase);

    public RecoverySource(string profile)
    {
        if (!Path.IsPathFullyQualified(profile))
        {
            throw new SessionException("Recovery requires an explicitly named absolute profile path.");
        }
        ProfilePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        if (string.Equals(ProfilePath, Path.GetPathRoot(ProfilePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionException("A filesystem root is not a recovery profile.");
        }
        try
        {
            TrustedPath.EnsureNoReparsePoints(Path.GetPathRoot(ProfilePath)!, ProfilePath);
            _profile = TrustedPath.TryOpenValidatedDirectory(ProfilePath, expectedIdentity: null)
                ?? throw new SessionException(
                    "The selected profile is missing, inaccessible, or redirected. " +
                    "If access is denied, rerun recovery explicitly from an elevated terminal; no ACLs were changed.");
        }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            throw UnreadableSource(exception);
        }
    }

    public string ProfilePath { get; }

    public async Task<int> CopyAsync(
        IEnumerable<string> paths,
        string destination,
        SessionWorkspaceOperation operation,
        CancellationToken cancellationToken)
    {
        int copied = 0;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string absolute = Path.GetFullPath(path);
            string relative = StateTransferFiles.Relative(ProfilePath, absolute);
            StateTransferFiles.ValidateProfileAsset(relative);
            if (_copied.Contains(absolute))
            {
                continue;
            }
            try
            {
                TrustedPath.EnsureNoReparsePoints(ProfilePath, absolute);
                FileAttributes attributes = File.GetAttributes(absolute);
                List<string> entries = [absolute];
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    entries.AddRange(StateTransferFiles.Entries(absolute));
                    _inventories.Add(absolute, [.. entries]);
                }
                foreach (string entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_copied.Contains(entry))
                    {
                        continue;
                    }
                    string target = Path.Combine(destination, StateTransferFiles.Relative(ProfilePath, entry));
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    {
                        TrustedPath.ValidatedDirectory directory =
                            TrustedPath.TryOpenValidatedDirectory(entry, expectedIdentity: null)
                            ?? throw new SessionException("An offline source directory changed or became inaccessible.");
                        _directories.Add(directory);
                        operation.EnsureDirectory(target);
                    }
                    else
                    {
                        FileStream input = TrustedPath.OpenRead(ProfilePath, entry, protectContents: true);
                        _files.Add(entry, input);
                        operation.EnsureDirectory(Path.GetDirectoryName(target)!);
                        using Stream output = operation.CreateNew(target);
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    _copied.Add(entry);
                    copied++;
                }
            }
            catch (Exception exception) when (IsAccessDenied(exception))
            {
                throw UnreadableSource(exception);
            }
            catch (IOException exception) when (
                exception.InnerException is Win32Exception { NativeErrorCode: 32 or 33 })
            {
                throw new SessionException(
                    "The selected profile is still in use. Stop its writers explicitly, then retry offline recovery. " +
                    "No source process was terminated.", exception);
            }
        }
        EnsureUnchanged();
        return copied;
    }

    public void EnsureUnchanged()
    {
        try
        {
            using TrustedPath.ValidatedDirectory current =
                TrustedPath.TryOpenValidatedDirectory(ProfilePath, _profile.Identity) ??
                    throw new SessionException("The selected source profile changed during recovery.");
            foreach ((string root, string[] expected) in _inventories)
            {
                string[] currentEntries = [root, .. StateTransferFiles.Entries(root)];
                if (!expected.SequenceEqual(currentEntries, StringComparer.OrdinalIgnoreCase))
                {
                    throw new SessionException("The offline source inventory changed during recovery. Stop its writers before retrying.");
                }
            }
        }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            throw UnreadableSource(exception);
        }
    }

    public void Dispose()
    {
        foreach (FileStream file in _files.Values)
        {
            file.Dispose();
        }
        foreach (TrustedPath.ValidatedDirectory directory in _directories)
        {
            directory.Dispose();
        }
        _profile.Dispose();
    }

    private static bool IsAccessDenied(Exception exception) =>
        exception is UnauthorizedAccessException ||
        exception is IOException { InnerException: Win32Exception { NativeErrorCode: 5 } };

    private static SessionException UnreadableSource(Exception exception) => new(
        "The selected old profile is not readable. Rerun recovery explicitly from an elevated terminal. " +
        "The source and its ACLs were not changed.", exception);
}
