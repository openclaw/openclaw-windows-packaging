namespace OpenClaw.Launcher.Session;

/// <summary>Rejects path redirection below a caller-established root.</summary>
internal static class TrustedPath
{
    public static void EnsureNoReparsePoints(string trustedRoot, string candidatePath) =>
        EnsureNoReparsePoints(trustedRoot, candidatePath, File.GetAttributes);

    internal static void EnsureNoReparsePoints(
        string trustedRoot,
        string candidatePath,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentNullException.ThrowIfNull(getAttributes);

        string root = Path.GetFullPath(trustedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(candidatePath);
        string prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionException(
                $"The path resolves outside its trusted root: {candidate}");
        }

        Check(root);
        string relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
        {
            return;
        }

        string current = root;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Check(current);
        }

        void Check(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = getAttributes(path);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SessionException(
                    $"The path contains a reparse point and is unsafe for host access: {path}");
            }
        }
    }
}
