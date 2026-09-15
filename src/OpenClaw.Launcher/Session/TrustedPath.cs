using Microsoft.Win32.SafeHandles;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace OpenClaw.Launcher.Session;

/// <summary>Rejects path redirection below a caller-established root.</summary>
internal static partial class TrustedPath
{
    internal readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow);

    internal sealed class ValidatedDirectory : IDisposable
    {
        internal ValidatedDirectory(
            SafeFileHandle handle,
            FileIdentity identity,
            string finalPath)
        {
            Handle = handle;
            Identity = identity;
            FinalPath = finalPath;
        }

        internal SafeFileHandle Handle { get; }

        internal FileIdentity Identity { get; }

        internal string FinalPath { get; }

        public void Dispose() => Handle.Dispose();
    }

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
    /// <summary>
    /// Opens a file only when the object opened by the host is still below the
    /// trusted root and is not a reparse point.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The FileStream constructor takes ownership of the SafeFileHandle.")]
    public static FileStream OpenRead(string trustedRoot, string candidatePath)
    {
        EnsureNoReparsePoints(trustedRoot, candidatePath);

        SafeFileHandle handle = CreateFile(
            candidatePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"The trusted file could not be opened: {candidatePath}",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SessionException(
                    $"The opened path is a reparse point: {candidatePath}");
            }

            string root = Path.GetFullPath(trustedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string opened = GetFinalPath(handle)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = root + Path.DirectorySeparatorChar;
            if (!opened.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !opened.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionException(
                    $"The opened file resolves outside its trusted root: {opened}");
            }

            return new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The FileStream constructor takes ownership of the SafeFileHandle.")]
    internal static FileStream CreateNew(ValidatedDirectory root, string candidatePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateParent(root, candidatePath);

        SafeFileHandle handle = CreateFile(
            candidatePath,
            GenericWrite | FileReadAttributes,
            0,
            IntPtr.Zero,
            CreateNewDisposition,
            FileFlagOpenReparsePoint | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"The trusted file could not be created: {candidatePath}",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0 ||
                !IsBelowRoot(root.FinalPath, NormalizePath(GetFinalPath(handle))))
            {
                throw new SessionException(
                    $"The created file resolves outside its trusted root: {candidatePath}");
            }

            return new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Each directory handle is owned by a using declaration before the next statement can observe it.")]
    internal static void EnsureDirectory(ValidatedDirectory root, string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(root);

        string target = Path.GetFullPath(directoryPath);
        string relative = Path.GetRelativePath(root.FinalPath, target);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new SessionException(
                $"The directory resolves outside its trusted root: {target}");
        }

        string current = root.FinalPath;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Directory.CreateDirectory(current);
            using ValidatedDirectory directory = TryOpenValidatedDirectory(
                current,
                expectedIdentity: null) ?? throw new SessionException(
                    $"The directory resolves outside its trusted root: {current}");
            if (!IsBelowOrEqualRoot(root.FinalPath, directory.FinalPath))
            {
                throw new SessionException(
                    $"The directory resolves outside its trusted root: {current}");
            }
        }
    }

    internal static FileIdentity? TryGetDirectoryIdentity(string path)
    {
        using ValidatedDirectory? directory = TryOpenValidatedDirectory(path, expectedIdentity: null);
        return directory?.Identity;
    }

    internal static ValidatedDirectory? TryOpenValidatedDirectory(
        string path,
        FileIdentity? expectedIdentity)
    {
        SafeFileHandle handle = CreateFile(
            path,
            Delete | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        try
        {
            if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
            {
                handle.Dispose();
                return null;
            }

            FileIdentity identity = GetIdentity(handle);
            if (expectedIdentity is not null && identity != expectedIdentity)
            {
                handle.Dispose();
                return null;
            }

            return new ValidatedDirectory(handle, identity, NormalizePath(GetFinalPath(handle)));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static bool TryDeleteOwnedEntry(
        ValidatedDirectory root,
        string candidatePath,
        bool deleteReparsePointLeaf = false)
    {
        ValidateParent(root, candidatePath);

        SafeFileHandle handle = CreateFile(
            candidatePath,
            Delete | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        using (handle)
        {
            FileAttributes attributes = File.GetAttributes(handle);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return deleteReparsePointLeaf && MarkForDeletion(handle);
            }

            if (!IsBelowRoot(root.FinalPath, NormalizePath(GetFinalPath(handle))))
            {
                return false;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(candidatePath))
                {
                    TryDeleteOwnedEntry(root, child, deleteReparsePointLeaf);
                }
            }

            return MarkForDeletion(handle);
        }
    }

    private static void ValidateParent(ValidatedDirectory root, string candidatePath)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(candidatePath));
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new SessionException(
                $"The trusted file path has no parent directory: {candidatePath}");
        }

        using ValidatedDirectory directory = TryOpenValidatedDirectory(parent, expectedIdentity: null)
            ?? throw new SessionException(
                $"The trusted file parent resolves outside its trusted root: {parent}");
        if (!IsBelowOrEqualRoot(root.FinalPath, directory.FinalPath))
        {
            throw new SessionException(
                $"The trusted file parent resolves outside its trusted root: {parent}");
        }
    }

    private static bool MarkForDeletion(SafeFileHandle handle)
    {
        FileDispositionInformation disposition = new() { DeleteFile = true };
        return SetFileInformationByHandle(
            handle,
            FileDispositionInfo,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>());
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        char[] path = new char[260];
        uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Length, 0);
        if (length == 0)
        {
            throw new IOException("The opened file's final path could not be determined.");
        }

        if (length >= path.Length)
        {
            path = new char[checked((int)length + 1)];
            length = GetFinalPathNameByHandle(handle, path, (uint)path.Length, 0);
            if (length == 0 || length >= path.Length)
            {
                throw new IOException("The opened file's final path is too long.");
            }
        }

        const string extendedPathPrefix = @"\\?\";
        string value = new(path, 0, checked((int)length));
        return value.StartsWith(extendedPathPrefix, StringComparison.Ordinal)
            ? value[extendedPathPrefix.Length..]
            : value;
    }

    private static FileIdentity GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw new IOException("The opened path's identity could not be determined.");
        }

        return new FileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow);
    }

    private static string NormalizePath(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsBelowRoot(string root, string candidate)
    {
        string prefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBelowOrEqualRoot(string root, string candidate) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        IsBelowRoot(root, candidate);

    private const uint Delete = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint CreateNewDisposition = 1;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int FileDispositionInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint length,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);
}
