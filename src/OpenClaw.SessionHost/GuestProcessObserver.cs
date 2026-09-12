using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenClaw.SessionHost;

/// <summary>
/// Maps a listening TCP port to the process that owns it.
/// </summary>
/// <remarks>
/// <c>IPGlobalProperties.GetActiveTcpListeners</c> reports ports but not their
/// owners, and an open port with an unknown owner proves nothing: any program
/// in the session could have opened it. The extended table is the only way to
/// ask who is actually listening.
/// </remarks>
internal static class TcpListenerOwnership
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint InsufficientBuffer = 122;

    /// <summary>
    /// Returns the identifiers of processes listening on <paramref name="port"/>.
    /// </summary>
    public static IReadOnlyList<int> GetListenerProcessIds(int port)
    {
        List<int> owners = [];
        Collect(AfInet, port, owners);
        Collect(AfInet6, port, owners);
        return owners;
    }

    private static void Collect(int addressFamily, int port, List<int> owners)
    {
        uint size = 0;
        uint status = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            order: false,
            addressFamily,
            TcpTableOwnerPidListener,
            reserved: 0);

        if (status != InsufficientBuffer || size == 0)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = GetExtendedTcpTable(
                buffer,
                ref size,
                order: false,
                addressFamily,
                TcpTableOwnerPidListener,
                reserved: 0);

            if (status != 0)
            {
                return;
            }

            int count = Marshal.ReadInt32(buffer);
            int rowSize = addressFamily == AfInet
                ? Marshal.SizeOf<TcpRowOwnerPid>()
                : Marshal.SizeOf<Tcp6RowOwnerPid>();
            IntPtr row = buffer + 4;

            for (int index = 0; index < count; index++)
            {
                (int rowPort, int owner) = addressFamily == AfInet
                    ? Read(Marshal.PtrToStructure<TcpRowOwnerPid>(row))
                    : Read(Marshal.PtrToStructure<Tcp6RowOwnerPid>(row));

                if (rowPort == port && !owners.Contains(owner))
                {
                    owners.Add(owner);
                }

                row += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (int Port, int Owner) Read(TcpRowOwnerPid row) =>
        (HostPort(row.LocalPort), row.OwningProcessId);

    private static (int Port, int Owner) Read(Tcp6RowOwnerPid row) =>
        (HostPort(row.LocalPort), row.OwningProcessId);

    /// <summary>
    /// The table stores the port in network byte order inside a 32-bit field,
    /// so the two low bytes have to be swapped.
    /// </summary>
    private static int HostPort(uint value) =>
        (int)(((value & 0xFF) << 8) | ((value & 0xFF00) >> 8));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U4)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public int OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public int OwningProcessId;
    }
}

/// <summary>
/// Answers whether a process is a descendant of another.
/// </summary>
internal static class ProcessAncestry
{
    private const int Th32CsSnapProcess = 2;

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="ancestor"/> or
    /// is descended from it.
    /// </summary>
    /// <remarks>
    /// The gateway is a child of the supervisor rather than the supervisor
    /// itself, so an ownership check that only compared identifiers would
    /// reject every real gateway.
    /// </remarks>
    public static bool IsSelfOrDescendant(int candidate, int ancestor)
    {
        if (candidate == ancestor)
        {
            return true;
        }

        Dictionary<int, int> parents = ReadParents();
        int current = candidate;
        var seen = new HashSet<int>();

        // Bounded by the set of visited identifiers, because the snapshot can
        // contain a cycle once identifiers are reused.
        while (parents.TryGetValue(current, out int parent) && seen.Add(current))
        {
            if (parent == ancestor)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private static Dictionary<int, int> ReadParents()
    {
        Dictionary<int, int> parents = [];
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return parents;
        }

        try
        {
            ProcessEntry32 entry = default;
            entry.Size = Marshal.SizeOf<ProcessEntry32>();

            if (!Process32FirstW(snapshot, ref entry))
            {
                return parents;
            }

            do
            {
                parents[entry.ProcessId] = entry.ParentProcessId;
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return parents;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(
        IntPtr snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(
        IntPtr snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public int Size;
        public int Usage;
        public int ProcessId;
        public IntPtr DefaultHeapId;
        public int ModuleId;
        public int Threads;
        public int ParentProcessId;
        public int PriorityClassBase;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }
}

/// <summary>
/// Observes a recorded gateway from inside the session.
/// </summary>
internal static class GuestProcessObserver
{
    /// <summary>
    /// Returns the process's creation time, or null when no such process
    /// exists or it cannot be examined.
    /// </summary>
    public static DateTimeOffset? GetStartTimeUtc(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the recorded supervisor, or a descendant of it, is listening
    /// on the port.
    /// </summary>
    public static bool OwnsListenerOn(int port, int supervisorProcessId)
    {
        if (port <= 0)
        {
            return false;
        }

        try
        {
            foreach (int owner in TcpListenerOwnership.GetListenerProcessIds(port))
            {
                if (ProcessAncestry.IsSelfOrDescendant(owner, supervisorProcessId))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            DllNotFoundException or EntryPointNotFoundException)
        {
            // An unanswerable question is reported as not-owned rather than
            // assumed either way.
            return false;
        }

        return false;
    }

    public static bool AnythingListeningOn(int port)
    {
        if (port <= 0)
        {
            return false;
        }

        try
        {
            return TcpListenerOwnership.GetListenerProcessIds(port).Count > 0;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
