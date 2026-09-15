namespace OpenClaw.Launcher.Session;

/// <summary>Deletes only the contents of the two package-owned state roots.</summary>
internal sealed class InstallationStateCleaner : IInstallationStateCleaner
{
    private readonly string[] _roots;
    private readonly IInstallationFileSystem _fileSystem;

    public InstallationStateCleaner(HostPaths paths, string productLocalStateRoot)
        : this([paths.StateRoot, productLocalStateRoot], paths.PackageFamilyName)
    {
    }

    internal InstallationStateCleaner(
        IReadOnlyList<string> trustedRoots,
        string? packageFamilyName = "test",
        IInstallationFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(trustedRoots);
        _fileSystem = fileSystem ?? PhysicalInstallationFileSystem.Instance;
        if (packageFamilyName is null)
        {
            throw new SessionException(
                "OpenClaw is not running from its installed package, so --fresh is unavailable.");
        }

        _roots = [.. trustedRoots.Select(root => ValidateRoot(root, _fileSystem))];
        if (_roots[0].StartsWith(_roots[1] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            _roots[1].StartsWith(_roots[0] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionException("The installation state roots overlap and are unsafe to clear.");
        }
    }

    public void Clear()
    {
        foreach (string root in _roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_fileSystem.DirectoryExists(root))
            {
                continue;
            }

            foreach (string entry in _fileSystem.EnumerateFileSystemEntries(root))
            {
                DeleteEntry(entry, _fileSystem);
            }
        }
    }

    private static string ValidateRoot(string root, IInstallationFileSystem fileSystem)
    {
        string fullRoot = Path.GetFullPath(root);
        string? parent = Directory.GetParent(fullRoot)?.FullName;
        if (parent is null || string.Equals(fullRoot, parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionException("The installation state root is unsafe to clear.");
        }

        if (fileSystem.DirectoryExists(fullRoot) &&
            (fileSystem.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new SessionException("The installation state root is a reparse point and cannot be cleared.");
        }

        return fullRoot;
    }

    private static void DeleteEntry(string path, IInstallationFileSystem fileSystem)
    {
        FileAttributes attributes = fileSystem.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0)
            {
                fileSystem.DeleteDirectory(path);
            }
            else
            {
                fileSystem.DeleteFile(path);
            }

            return;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            fileSystem.DeleteFile(path);
            return;
        }

        foreach (string child in fileSystem.EnumerateFileSystemEntries(path))
        {
            DeleteEntry(child, fileSystem);
        }

        fileSystem.DeleteDirectory(path);
    }
}

internal interface IInstallationFileSystem
{
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    FileAttributes GetAttributes(string path);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
}

internal sealed class PhysicalInstallationFileSystem : IInstallationFileSystem
{
    public static PhysicalInstallationFileSystem Instance { get; } = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path) => Directory.Delete(path);
}
