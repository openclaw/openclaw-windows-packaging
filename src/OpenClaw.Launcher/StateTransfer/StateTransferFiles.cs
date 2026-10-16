using System.Security.Cryptography;
using System.Text;

#if OPENCLAW_SESSION_HOST
using OpenClaw.SessionProtocol;
namespace OpenClaw.SessionHost;
#else
using OpenClaw.Launcher.Session;
using SessionLaunchException = OpenClaw.Launcher.Session.SessionException;
namespace OpenClaw.Launcher.StateTransfer;
#endif

internal static class StateTransferFiles
{
    private const int MaximumEntries = 1_000_000;

    public static string Relative(string root, string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new SessionLaunchException("A state asset has no absolute source path.");
        }
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        ValidateRelative(relative);
        return relative;
    }

    public static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new SessionLaunchException("A state asset has an unsafe relative path.");
        }
        foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            string stem = segment.Split('.')[0].ToUpperInvariant();
            if (segment.Length == 0 || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
            {
                throw new SessionLaunchException("A state asset has an unsafe Windows path segment.");
            }
        }
    }

    public static void ValidateProfileAsset(string relative)
    {
        ValidateRelative(relative);
        string first = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])[0];
        if (first.Equals("AppData", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("NTUSER", StringComparison.OrdinalIgnoreCase) ||
            first.Equals(".openclaw-backups", StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionLaunchException(
                "A state asset would replace Windows, package, or retained-backup data.");
        }
    }

    public static bool Covers(string root, string path) =>
        string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static List<string> Entries(string root)
    {
        TrustedPath.EnsureNoReparsePoints(Path.GetPathRoot(root)!, root);
        List<string> result = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        Visit(root);
        return result;

        void Visit(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                string relative = Path.GetRelativePath(root, entry);
                ValidateRelative(relative);
                if (!paths.Add(relative))
                {
                    throw new SessionLaunchException("The state tree contains colliding Windows paths.");
                }
                TrustedPath.EnsureNoReparsePoints(root, entry);
                result.Add(entry);
                if (result.Count > MaximumEntries)
                {
                    throw new SessionLaunchException("The state tree exceeds the supported one-million-entry limit.");
                }
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                {
                    Visit(entry);
                }
            }
        }
    }

    public static string Digest(string root)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string entry in Entries(root))
        {
            string relative = Path.GetRelativePath(root, entry).Replace('\\', '/').ToUpperInvariant();
            bool directory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
            hash.AppendData(Encoding.UTF8.GetBytes((directory ? "d:" : "f:") + relative + "\0"));
            if (!directory)
            {
                using FileStream file = TrustedPath.OpenRead(root, entry, protectContents: true);
                hash.AppendData(SHA256.HashData(file));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void Copy(string source, string destination)
    {
        TrustedPath.EnsureNoReparsePoints(Path.GetPathRoot(destination)!, destination);
        Directory.CreateDirectory(destination);
        using TrustedPath.ValidatedDirectory root =
            TrustedPath.TryOpenValidatedDirectory(destination, expectedIdentity: null)
            ?? throw new SessionLaunchException("The state staging directory cannot be opened safely.");
        foreach (string entry in Entries(source))
        {
            string target = Path.Combine(destination, Relative(source, entry));
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
            {
                TrustedPath.EnsureDirectory(root, target);
            }
            else
            {
                CopyFile(root, source, entry, target);
            }
        }
    }

    private static void CopyFile(
        TrustedPath.ValidatedDirectory root,
        string sourceRoot,
        string source,
        string target)
    {
        using FileStream input = TrustedPath.OpenRead(sourceRoot, source, protectContents: true);
        using Stream output = TrustedPath.CreateNew(root, target);
        input.CopyTo(output);
        output.Flush();
    }

    public static void Delete(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        _ = Entries(root);
        Directory.Delete(root, recursive: true);
    }
}
