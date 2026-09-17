using System.Text;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// Owns one host/guest exchange in the shared session workspace.
/// </summary>
/// <remarks>
/// The workspace is guest-writable, so callers must not authorize an operation
/// from paths alone. This contract pins the recorded sandbox generation,
/// validates the opened workspace object, rechecks that the same generation is
/// still current before each effect, and opens request/result files through
/// handle-bound TrustedPath helpers.
/// </remarks>
internal sealed class SessionWorkspaceOperation : IDisposable
{
    private readonly SessionRecord _record;
    private readonly Func<SessionRecord, bool> _isCurrent;
    private readonly TrustedPath.ValidatedDirectory _workspace;

    public SessionWorkspaceOperation(
        SessionRecord record,
        Func<SessionRecord, bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(isCurrent);

        if (string.IsNullOrWhiteSpace(record.WorkspacePath))
        {
            throw new SessionException(
                "The recorded session has no shared workspace, so an operation " +
                "cannot be delivered to it.");
        }

        if (string.IsNullOrWhiteSpace(record.Generation))
        {
            throw new SessionException(
                "The recorded session has no operation generation. Run `clawctl setup` again.");
        }

        _record = record;
        _isCurrent = isCurrent;
        _workspace = TrustedPath.TryOpenValidatedDirectory(record.WorkspacePath, expectedIdentity: null)
            ?? throw new SessionException(
                $"The recorded shared workspace could not be opened safely: {record.WorkspacePath}");
        try
        {
            EnsureCurrent();
        }
        catch
        {
            _workspace.Dispose();
            throw;
        }
    }

    public string WorkspacePath => _workspace.FinalPath;

    public string FilePath(string prefix, string requestId, string suffix = ".json")
    {
        VerifyName(prefix, nameof(prefix));
        VerifyName(requestId, nameof(requestId));
        if (suffix.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            suffix.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new SessionException($"The operation suffix is not a file suffix: {suffix}");
        }

        return Path.Combine(
            WorkspacePath,
            $"{prefix}-{_record.Generation}-{requestId}{suffix}");
    }

    public async Task WriteTextNewAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        EnsureCurrent();
        using Stream stream = TrustedPath.CreateNew(_workspace, path);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using FileStream stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public FileStream OpenRead(string path)
    {
        EnsureCurrent();
        return TrustedPath.OpenRead(WorkspacePath, path);
    }

    public Stream CreateNew(string path)
    {
        EnsureCurrent();
        return TrustedPath.CreateNew(_workspace, path);
    }

    public void EnsureDirectory(string path)
    {
        EnsureCurrent();
        TrustedPath.EnsureDirectory(_workspace, path);
    }

    public void Delete(string path)
    {
        EnsureCurrent();
        try
        {
            _ = TrustedPath.TryDeleteOwnedEntry(
                _workspace,
                path,
                deleteReparsePointLeaf: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void EnsureCurrent()
    {
        if (!_isCurrent(_record))
        {
            throw new SessionException(
                "The recorded isolated-session generation changed before the operation completed. Retry the command.");
        }
    }

    public void Dispose() => _workspace.Dispose();

    private static void VerifyName(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new SessionException($"The operation {name} is not a single safe path segment.");
        }
    }
}
