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

    private sealed class ProtectedDirectoryChain : IDisposable
    {
        private readonly List<ValidatedDirectory> _directories = [];

        public ValidatedDirectory Leaf => _directories[^1];

        public void Add(ValidatedDirectory directory) => _directories.Add(directory);

        public void Dispose()
        {
            for (int index = _directories.Count - 1; index >= 0; index--)
            {
                _directories[index].Dispose();
            }
        }
    }

    private sealed class ProtectedStream(
        FileStream stream,
        ProtectedDirectoryChain directories) : Stream
    {
        public override bool CanRead => stream.CanRead;

        public override bool CanSeek => stream.CanSeek;

        public override bool CanWrite => stream.CanWrite;

        public override long Length => stream.Length;

        public override long Position
        {
            get => stream.Position;
            set => stream.Position = value;
        }

        public override void Flush() => stream.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            stream.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            stream.Seek(offset, origin);

        public override void SetLength(long value) => stream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            stream.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            stream.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stream.Dispose();
                directories.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            directories.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
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
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                throw new FileNotFoundException(
                    $"The trusted file does not exist: {candidatePath}",
                    candidatePath);
            }

            throw new IOException(
                $"The trusted file could not be opened: {candidatePath}",
                new System.ComponentModel.Win32Exception(error));
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
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned protected stream owns both the file and directory-chain handles.")]
    internal static Stream CreateNew(ValidatedDirectory root, string candidatePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        string? parent = Path.GetDirectoryName(Path.GetFullPath(candidatePath));
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new SessionException(
                $"The trusted file path has no parent directory: {candidatePath}");
        }

        ProtectedDirectoryChain protectedParent = ProtectDirectoryChain(
            root,
            parent,
            createMissing: false);

        try
        {
            SafeFileHandle handle = CreateRelative(
                protectedParent.Leaf.Handle,
                Path.GetFileName(candidatePath),
                GenericWrite | FileReadAttributes,
                shareAccess: 0,
                FileCreate,
                FileNonDirectoryFile | FileOpenReparsePoint);

            FileStream? stream = null;
            try
            {
                if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0 ||
                    !IsBelowRoot(root.FinalPath, NormalizePath(GetFinalPath(handle))))
                {
                    throw new SessionException(
                        $"The created file resolves outside its trusted root: {candidatePath}");
                }

                stream = new FileStream(
                    handle,
                    FileAccess.Write,
                    bufferSize: 4096,
                    isAsync: true);
                return new ProtectedStream(stream, protectedParent);
            }
            catch
            {
                stream?.Dispose();
                if (stream is null)
                {
                    handle.Dispose();
                }

                throw;
            }
        }
        catch
        {
            protectedParent.Dispose();
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

        using ProtectedDirectoryChain protectedDirectories = ProtectDirectoryChain(
            root,
            target,
            createMissing: true);
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
            GenericRead | FileReadAttributes,
            FileShareRead | FileShareWrite,
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

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The relative handle is immediately owned by the using statement below.")]
    internal static bool TryDeleteOwnedEntry(
        ValidatedDirectory root,
        string candidatePath,
        bool deleteReparsePointLeaf = false)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(candidatePath));
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        using ProtectedDirectoryChain protectedParent = ProtectDirectoryChain(
            root,
            parent,
            createMissing: false);

        string name = Path.GetFileName(candidatePath);
        SafeFileHandle handle;
        try
        {
            handle = CreateRelative(
                protectedParent.Leaf.Handle,
                name,
                GenericRead | FileReadAttributes,
                FileShareRead | FileShareWrite,
                FileOpen,
                FileOpenReparsePoint);
        }
        catch (IOException)
        {
            return false;
        }

        FileAttributes attributes;
        using (handle)
        {
            attributes = File.GetAttributes(handle);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (!deleteReparsePointLeaf)
                {
                    return false;
                }
            }
            else if (!IsBelowRoot(root.FinalPath, NormalizePath(GetFinalPath(handle))))
            {
                return false;
            }

            if ((attributes & FileAttributes.Directory) != 0 &&
                (attributes & FileAttributes.ReparsePoint) == 0)
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(candidatePath))
                {
                    TryDeleteOwnedEntry(root, child, deleteReparsePointLeaf);
                }
            }
        }

        try
        {
            using SafeFileHandle deleteHandle = CreateRelative(
                protectedParent.Leaf.Handle,
                name,
                Delete | FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                FileOpenReparsePoint);
            return MarkForDeletion(deleteHandle);
        }
        catch (IOException)
        {
            return false;
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Each validated directory is transferred to the returned chain.")]
    private static ProtectedDirectoryChain ProtectDirectoryChain(
        ValidatedDirectory root,
        string directoryPath,
        bool createMissing)
    {
        string target = Path.GetFullPath(directoryPath);
        string relative = Path.GetRelativePath(root.FinalPath, target);
        if (relative.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new SessionException(
                $"The directory resolves outside its trusted root: {target}");
        }

        ProtectedDirectoryChain chain = ProtectRoot(root);
        try
        {
            if (relative == ".")
            {
                return chain;
            }

            string current = root.FinalPath;
            foreach (string segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                ValidatedDirectory directory = OpenRelativeDirectory(
                    chain.Leaf.Handle,
                    segment,
                    createMissing);
                if (!IsBelowOrEqualRoot(root.FinalPath, directory.FinalPath))
                {
                    directory.Dispose();
                    throw new SessionException(
                        $"The directory resolves outside its trusted root: {current}");
                }

                chain.Add(directory);
            }

            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned chain owns the validated root directory.")]
    private static ProtectedDirectoryChain ProtectRoot(ValidatedDirectory root)
    {
        var chain = new ProtectedDirectoryChain();
        ValidatedDirectory protectedRoot = TryOpenProtectedDirectory(
            root.FinalPath,
            root.Identity) ?? throw new SessionException(
                $"The trusted root changed before the host operation: {root.FinalPath}");
        chain.Add(protectedRoot);
        return chain;
    }

    private static ValidatedDirectory? TryOpenProtectedDirectory(
        string path,
        FileIdentity? expectedIdentity)
    {
        SafeFileHandle handle = CreateFile(
            path,
            GenericRead | FileReadAttributes,
            FileShareRead | FileShareWrite,
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

    private static ValidatedDirectory OpenRelativeDirectory(
        SafeFileHandle parent,
        string name,
        bool createMissing)
    {
        SafeFileHandle handle = CreateRelative(
            parent,
            name,
            GenericRead | FileReadAttributes,
            FileShareRead | FileShareWrite,
            createMissing ? FileOpenIf : FileOpen,
            FileDirectoryFile | FileOpenReparsePoint);
        try
        {
            if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SessionException(
                    $"The directory contains a reparse point: {name}");
            }

            return new ValidatedDirectory(
                handle,
                GetIdentity(handle),
                NormalizePath(GetFinalPath(handle)));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle CreateRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint shareAccess,
        uint createDisposition,
        uint createOptions)
    {
        IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
        try
        {
            var unicodeName = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            IntPtr unicodeNamePointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(unicodeName, unicodeNamePointer, fDeleteOld: false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = unicodeNamePointer,
                    Attributes = ObjectCaseInsensitive
                };
                uint status = NtCreateFile(
                    out SafeFileHandle handle,
                    desiredAccess,
                    ref attributes,
                    out _,
                    IntPtr.Zero,
                    FileAttributeNormal,
                    shareAccess,
                    createDisposition,
                    createOptions,
                    IntPtr.Zero,
                    0);
                GC.KeepAlive(parent);
                if (unchecked((int)status) < 0)
                {
                    handle.Dispose();
                    int error = checked((int)RtlNtStatusToDosError(status));
                    throw new IOException(
                        $"The trusted relative path could not be opened: {name}",
                        new System.ComponentModel.Win32Exception(error));
                }

                return handle;
            }
            finally
            {
                Marshal.FreeHGlobal(unicodeNamePointer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuffer);
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
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public nuint Information;
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

    [DllImport("ntdll.dll")]
    private static extern uint NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(uint status);

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
