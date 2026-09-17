using Microsoft.Win32.SafeHandles;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The <c>--collect</c> mode: stages named diagnostic files into the shared
/// workspace so the host can read them.
/// </summary>
/// <remarks>
/// <para>
/// The agent profile is ACL'd against the invoking user. Without this the host
/// cannot read the agent's own OpenClaw logs at all, which is exactly the state
/// someone is in when they need to report a problem.
/// </para>
/// <para>
/// Every source is named by the host. A missing source is reported, never
/// treated as a failure, because collection is most useful precisely when an
/// installation is incomplete.
/// </para>
/// </remarks>
internal static class SessionCollector
{
    private static readonly Guid ProfileFolderId =
        new("5E6C858F-0E22-4760-9AFE-EA3317B67173");

    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile,
        string? profileRoot = null,
        Func<string, string, SearchOption, IEnumerable<string>>? enumerateFiles = null,
        Action? beforeDestinationOpen = null)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;
        string profile = profileRoot ?? GetProfilePath();

        try
        {
            SessionCollectRequest request =
                SessionCollectProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId;
            string destination = ResolveInWorkspace(
                Path.GetDirectoryName(Path.GetFullPath(requestPath))
                ?? throw new SessionLaunchException(
                    "has no shared workspace"),
                request.DestinationDirectory!);
            request = request with { DestinationDirectory = destination };

            using (ProtectDirectoryBelow(
                Path.GetDirectoryName(Path.GetFullPath(requestPath))!,
                destination,
                createMissing: true))
            {
            }
            List<SessionCollectEntry> entries = [];

            foreach (SessionCollectSource source in request.Sources ?? [])
            {
                Collect(
                    request,
                    source,
                    profile,
                    entries,
                    enumerateFiles ?? Directory.EnumerateFiles,
                    beforeDestinationOpen);
            }

            writeFile(
                resultPath,
                SessionCollectProtocol.SerializeResult(new SessionCollectResult
                {
                    RequestId = requestId,
                    Entries = entries
                }));
            return 0;
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            TryWriteFailure(writeFile, resultPath, requestId, exception.Message);
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }

    private static void Collect(
        SessionCollectRequest request,
        SessionCollectSource source,
        string profile,
        List<SessionCollectEntry> entries,
        Func<string, string, SearchOption, IEnumerable<string>> enumerateFiles,
        Action? beforeDestinationOpen)
    {
        string path;
        try
        {
            path = ResolveInProfile(profile, source.RelativePath!);
        }
        catch (SessionLaunchException exception)
        {
            entries.Add(new SessionCollectEntry
            {
                Name = source.Name,
                Copied = false,
                Detail = exception.Message
            });
            return;
        }

        if (Directory.Exists(path))
        {
            CollectDirectory(
                request,
                source,
                profile,
                path,
                entries,
                enumerateFiles,
                beforeDestinationOpen);
            return;
        }

        if (!File.Exists(path))
        {
            entries.Add(new SessionCollectEntry
            {
                Name = source.Name,
                Copied = false,
                Detail = "not present"
            });
            return;
        }

        CopyOne(
            request,
            profile,
            path,
            source.Name!,
            entries,
            beforeDestinationOpen);
    }

    private static void CollectDirectory(
        SessionCollectRequest request,
        SessionCollectSource source,
        string profile,
        string root,
        List<SessionCollectEntry> entries,
        Func<string, string, SearchOption, IEnumerable<string>> enumerateFiles,
        Action? beforeDestinationOpen)
    {
        IEnumerable<string> files;
        try
        {
            files = enumerateFiles(
                root,
                string.IsNullOrWhiteSpace(source.Pattern) ? "*" : source.Pattern,
                source.Recursive
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            entries.Add(new SessionCollectEntry
            {
                Name = source.Name,
                Copied = false,
                Detail = $"unreadable: {exception.Message}"
            });
            return;
        }

        bool any = false;
        try
        {
            foreach (string file in files)
            {
                any = true;
                string relative = Path.GetRelativePath(root, file);
                CopyOne(
                    request,
                    profile,
                    file,
                    Path.Combine(source.Name!, relative).Replace('\\', '/'),
                    entries,
                    beforeDestinationOpen);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            entries.Add(new SessionCollectEntry
            {
                Name = source.Name,
                Copied = false,
                Detail = $"unreadable: {exception.Message}"
            });
            return;
        }

        if (!any)
        {
            entries.Add(new SessionCollectEntry
            {
                Name = source.Name,
                Copied = false,
                Detail = "empty"
            });
        }
    }

    /// <summary>
    /// Resolves a source against this account's profile.
    /// </summary>
    /// <remarks>
    /// Only the guest can do this: the profile directory is named after a
    /// generated account and is ACL'd against the host user. The resolved path
    /// is re-checked against the profile root because <c>Path.Combine</c>
    /// on a rooted segment silently discards the root it was given.
    /// </remarks>
    internal static string ResolveInProfile(string profile, string relativePath)
    {
        string resolved = Path.GetFullPath(Path.Combine(profile, relativePath));
        string root = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        return resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? resolved
            : throw new SessionLaunchException(
                "resolves outside the agent profile");
    }

    private static string GetProfilePath()
    {
        int result = SHGetKnownFolderPath(
            ProfileFolderId,
            flags: 0,
            token: IntPtr.Zero,
            out IntPtr path);
        if (result < 0)
        {
            throw new SessionLaunchException(
                $"Windows could not resolve the user profile directory " +
                $"(HRESULT 0x{result:X8}).");
        }

        try
        {
            return Marshal.PtrToStringUni(path)
                ?? throw new SessionLaunchException(
                    "Windows returned an empty user profile directory.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    private static string ResolveInWorkspace(string workspace, string destination)
    {
        string root = Path.GetFullPath(workspace)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string resolved = Path.GetFullPath(destination);
        string prefix = root + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionLaunchException(
                "destination resolves outside the shared workspace");
        }

        string relative = Path.GetRelativePath(root, resolved);
        string current = root;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SessionLaunchException(
                    "destination contains a reparse point");
            }
        }

        return resolved;
    }

    private static void CopyOne(
        SessionCollectRequest request,
        string profile,
        string sourcePath,
        string name,
        List<SessionCollectEntry> entries,
        Action? beforeDestinationOpen)
    {
        string destination = Path.Combine(
            request.DestinationDirectory!,
            name.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            using FileStream input = OpenReadBelow(profile, sourcePath, out string openedSource);
            if (SessionCollectProtocol.IsDenied(
                Path.GetFileName(openedSource),
                request.DeniedNames))
            {
                entries.Add(new SessionCollectEntry
                {
                    Name = name,
                    Copied = false,
                    Detail = "excluded by policy"
                });
                return;
            }

            // Shared read/write/delete because the agent's own OpenClaw
            // processes hold their logs open while they run; an exclusive open
            // would fail on exactly the live installation worth collecting.
            using ProtectedDirectoryChain destinationAuthority = ProtectDirectoryBelow(
                request.DestinationDirectory!,
                Path.GetDirectoryName(destination)!,
                createMissing: true);
            beforeDestinationOpen?.Invoke();
            RequireOpenedDirectoryBelow(
                destinationAuthority.Leaf,
                request.DestinationDirectory!);
            using FileStream output = OpenWriteBelow(
                destinationAuthority.Leaf,
                request.DestinationDirectory!,
                destination);
            input.CopyTo(output);

            entries.Add(new SessionCollectEntry
            {
                Name = name,
                Copied = true,
                Length = output.Length
            });
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            entries.Add(new SessionCollectEntry
            {
                Name = name,
                Copied = false,
                Detail = exception.Message
            });
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned FileStream takes ownership of the SafeFileHandle.")]
    private static FileStream OpenReadBelow(
        string root,
        string path,
        out string openedPath)
    {
        SafeFileHandle handle = CreateFile(
            path,
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
            throw new IOException(
                $"The diagnostic source could not be opened: {path}",
                new System.ComponentModel.Win32Exception(error));
        }

        try
        {
            openedPath = RequireOpenedFileBelow(handle, root, path);
            return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
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
        Justification = "The returned FileStream takes ownership of the SafeFileHandle.")]
    private static FileStream OpenWriteBelow(
        SafeFileHandle parent,
        string root,
        string path)
    {
        SafeFileHandle handle = CreateRelative(
            parent,
            Path.GetFileName(path),
            GenericWrite | FileReadAttributes,
            shareAccess: 0,
            FileCreate,
            FileNonDirectoryFile | FileOpenReparsePoint);

        try
        {
            _ = RequireOpenedFileBelow(handle, root, path);
            return new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RequireOpenedDirectoryBelow(
        SafeFileHandle handle,
        string root)
    {
        string opened = GetFinalPath(handle)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!opened.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !opened.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionLaunchException(
                "diagnostic destination resolves outside its staging directory");
        }
    }

    private static string RequireOpenedFileBelow(
        SafeFileHandle handle,
        string root,
        string path)
    {
        if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
        {
            throw new SessionLaunchException(
                $"The diagnostic path is a filesystem link: {path}");
        }

        string opened = GetFinalPath(handle);
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!opened.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionLaunchException(
                $"The diagnostic path resolves outside its trusted root: {path}");
        }

        return opened;
    }

    private sealed class ProtectedDirectoryChain : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];

        public SafeFileHandle Leaf => _handles[^1];

        public void Add(SafeFileHandle handle) => _handles.Add(handle);

        public void Dispose()
        {
            for (int index = _handles.Count - 1; index >= 0; index--)
            {
                _handles[index].Dispose();
            }
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Each validated directory handle is transferred to the returned chain.")]
    private static ProtectedDirectoryChain ProtectDirectoryBelow(
        string root,
        string path,
        bool createMissing)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string relative = Path.GetRelativePath(normalizedRoot, Path.GetFullPath(path));
        if (relative.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new SessionLaunchException(
                "diagnostic destination resolves outside its staging directory");
        }

        var chain = new ProtectedDirectoryChain();
        try
        {
            chain.Add(OpenProtectedDirectory(normalizedRoot, normalizedRoot));
            if (relative == ".")
            {
                return chain;
            }

            foreach (string segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                chain.Add(OpenRelativeDirectory(
                    chain.Leaf,
                    segment,
                    normalizedRoot,
                    createMissing));
            }

            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenProtectedDirectory(string path, string root) =>
        TryOpenProtectedDirectory(path, root) ?? throw new SessionLaunchException(
            "diagnostic destination contains a filesystem link");

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The caller owns the returned SafeFileHandle.")]
    private static SafeFileHandle? TryOpenProtectedDirectory(string path, string root)
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

            string opened = GetFinalPath(handle)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = normalizedRoot + Path.DirectorySeparatorChar;
            if (!opened.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !opened.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                handle.Dispose();
                return null;
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenRelativeDirectory(
        SafeFileHandle parent,
        string name,
        string root,
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
                throw new SessionLaunchException(
                    "diagnostic destination contains a filesystem link");
            }

            string opened = GetFinalPath(handle)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = normalizedRoot + Path.DirectorySeparatorChar;
            if (!opened.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !opened.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionLaunchException(
                    "diagnostic destination resolves outside its staging directory");
            }

            return handle;
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
                    desiredAccess | Synchronize,
                    ref attributes,
                    out _,
                    IntPtr.Zero,
                    FileAttributeNormal,
                    shareAccess,
                    createDisposition,
                    createOptions | FileSynchronousIoNonAlert,
                    IntPtr.Zero,
                    0);
                GC.KeepAlive(parent);
                if (unchecked((int)status) < 0)
                {
                    handle.Dispose();
                    int error = checked((int)RtlNtStatusToDosError(status));
                    throw new IOException(
                        $"The diagnostic relative path could not be opened: {name}",
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

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0)
        {
            throw new IOException(
                "The diagnostic path could not be resolved.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length + 1)];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length)
            {
                throw new IOException("The diagnostic path could not be resolved.");
            }
        }

        const string extendedPrefix = @"\\?\";
        string path = new(buffer, 0, checked((int)length));
        return path.StartsWith(extendedPrefix, StringComparison.Ordinal)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static void TryWriteFailure(
        Action<string, string> writeFile,
        string resultPath,
        string? requestId,
        string error)
    {
        try
        {
            writeFile(
                resultPath,
                SessionCollectProtocol.SerializeResult(new SessionCollectResult
                {
                    RequestId = requestId,
                    Error = error
                }));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The exit code is the only remaining channel.
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileCreate = 2;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint FileOpen = 1;
    private const uint FileOpenIf = 3;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

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
        uint pathLength,
        uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        in Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
