using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Reads and writes shortcuts through the shell's ShellLink object, which is
/// what Explorer itself reads when it starts Startup-folder items at sign-in.
/// </summary>
/// <remarks>
/// Source-generated COM keeps this NativeAOT-safe. ShellLink is registered for
/// both apartments, so each call joins the multithreaded apartment for its own
/// duration and leaves the calling thread as it found it.
/// </remarks>
internal sealed partial class ShellStartupLinkStore : IStartupLinkStore
{
    private const uint ClsctxInprocServer = 0x1;
    private const uint CoinitMultithreaded = 0x0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint StgmRead = 0x0;
    private const uint SlgpRawPath = 0x4;

    // INFOTIPSIZE, the longest description the shell stores; ample for a path.
    private const int TextCapacity = 1024;

    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterfaceId = new("000214F9-0000-0000-C000-000000000046");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private enum LinkText
    {
        TargetPath,
        Arguments,
        Description,
    }

    public StartupLink? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        return Use(path, "read", link =>
        {
            Check(((IPersistFile)link).Load(path, StgmRead), path, "read");
            return new StartupLink(
                ReadText(link, LinkText.TargetPath, path),
                ReadText(link, LinkText.Arguments, path),
                ReadText(link, LinkText.Description, path));
        });
    }

    public void Write(string path, StartupLink link)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(link);

        _ = Use(path, "write", shellLink =>
        {
            Check(shellLink.SetPath(link.TargetPath), path, "write");
            Check(shellLink.SetArguments(link.Arguments), path, "write");
            Check(shellLink.SetDescription(link.Description), path, "write");
            Check(((IPersistFile)shellLink).Save(path, remember: true), path, "write");
            return true;
        });
    }

    private static T Use<T>(string path, string operation, Func<IShellLinkW, T> action)
    {
        int initialized = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        if (initialized < 0 && initialized != RpcEChangedMode)
        {
            Check(initialized, path, operation);
        }

        try
        {
            Check(
                CoCreateInstance(
                    in ShellLinkClassId,
                    IntPtr.Zero,
                    ClsctxInprocServer,
                    in ShellLinkInterfaceId,
                    out IntPtr unknown),
                path,
                operation);
            object instance;
            try
            {
                instance = Wrappers.GetOrCreateObjectForComInstance(
                    unknown,
                    CreateObjectFlags.UniqueInstance);
            }
            finally
            {
                _ = Marshal.Release(unknown);
            }

            try
            {
                return action((IShellLinkW)instance);
            }
            finally
            {
                // Release before leaving the apartment rather than at finalization.
                ((ComObject)instance).FinalRelease();
            }
        }
        finally
        {
            if (initialized >= 0)
            {
                CoUninitialize();
            }
        }
    }

    private static unsafe string ReadText(IShellLinkW link, LinkText field, string path)
    {
        char* buffer = stackalloc char[TextCapacity];
        buffer[0] = '\0';
        int result = field switch
        {
            LinkText.TargetPath => link.GetPath(buffer, TextCapacity, IntPtr.Zero, SlgpRawPath),
            LinkText.Arguments => link.GetArguments(buffer, TextCapacity),
            _ => link.GetDescription(buffer, TextCapacity),
        };
        Check(result, path, "read");
        return new string(buffer);
    }

    private static void Check(int result, string path, string operation)
    {
        if (result < 0)
        {
            throw new IOException(
                $"Windows could not {operation} the shortcut '{path}' (HRESULT 0x{result:X8}).",
                result);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        IntPtr outer,
        uint context,
        in Guid interfaceId,
        out IntPtr instance);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal unsafe partial interface IShellLinkW
{
    [PreserveSig]
    int GetPath(char* file, int capacity, IntPtr findData, uint flags);

    [PreserveSig]
    int GetIDList(out IntPtr idList);

    [PreserveSig]
    int SetIDList(IntPtr idList);

    [PreserveSig]
    int GetDescription(char* description, int capacity);

    [PreserveSig]
    int SetDescription(string description);

    [PreserveSig]
    int GetWorkingDirectory(char* directory, int capacity);

    [PreserveSig]
    int SetWorkingDirectory(string directory);

    [PreserveSig]
    int GetArguments(char* arguments, int capacity);

    [PreserveSig]
    int SetArguments(string arguments);

    [PreserveSig]
    int GetHotkey(out ushort hotkey);

    [PreserveSig]
    int SetHotkey(ushort hotkey);

    [PreserveSig]
    int GetShowCmd(out int showCommand);

    [PreserveSig]
    int SetShowCmd(int showCommand);

    [PreserveSig]
    int GetIconLocation(char* iconPath, int capacity, out int iconIndex);

    [PreserveSig]
    int SetIconLocation(string iconPath, int iconIndex);

    [PreserveSig]
    int SetRelativePath(string relativePath, uint reserved);

    [PreserveSig]
    int Resolve(IntPtr window, uint flags);

    [PreserveSig]
    int SetPath(string file);
}

// IPersistFile derives from IPersist; its one method leads the vtable.
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    [PreserveSig]
    int GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    [PreserveSig]
    int Load(string fileName, uint mode);

    [PreserveSig]
    int Save(string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

    [PreserveSig]
    int SaveCompleted(string? fileName);

    [PreserveSig]
    int GetCurFile(out IntPtr fileName);
}
