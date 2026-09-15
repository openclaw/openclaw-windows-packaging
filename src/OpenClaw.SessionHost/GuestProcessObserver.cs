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
        foreach ((int rowPort, int owner) in GetListeners())
        {
            if (rowPort == port && !owners.Contains(owner))
            {
                owners.Add(owner);
            }
        }

        return owners;
    }

    /// <summary>
    /// Every listening port and the process that owns it.
    /// </summary>
    /// <remarks>
    /// Enumerating the whole table is what lets a caller ask which port a known
    /// process listens on, rather than only whether it listens on a port the
    /// caller already guessed. The gateway's port is chosen by OpenClaw from
    /// its own configuration, so guessing it here would be a second opinion
    /// about a value this package does not own.
    /// </remarks>
    public static IReadOnlyList<(int Port, int Owner)> GetListeners()
    {
        List<(int Port, int Owner)> listeners = [];
        Collect(AfInet, listeners);
        Collect(AfInet6, listeners);
        return listeners;
    }

    private static void Collect(int addressFamily, List<(int Port, int Owner)> listeners)
    {
        uint size = 0;
        uint status = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            order: false,
            addressFamily,
            TcpTableOwnerPidListener,
            reserved: 0);

        if (status != InsufficientBuffer && status != 0)
        {
            throw new System.ComponentModel.Win32Exception((int)status, "Unable to query TCP listener ownership.");
        }
        if (size == 0)
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
                throw new System.ComponentModel.Win32Exception((int)status, "Unable to read TCP listener ownership.");
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

                if (!listeners.Contains((rowPort, owner)))
                {
                    listeners.Add((rowPort, owner));
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
            DateTimeOffset? childStart = GuestProcessObserver.GetStartTimeUtc(current);
            DateTimeOffset? parentStart = GuestProcessObserver.GetStartTimeUtc(parent);
            if (childStart is null || parentStart is null || parentStart > childStart)
            {
                return false;
            }
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
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "Unable to inspect the gateway process tree.");
        }

        try
        {
            ProcessEntry32 entry = default;
            entry.Size = Marshal.SizeOf<ProcessEntry32>();

            if (!Process32FirstW(snapshot, ref entry))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    "Unable to read the gateway process tree.");
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

        foreach (int owner in TcpListenerOwnership.GetListenerProcessIds(port))
        {
            if (ProcessAncestry.IsSelfOrDescendant(owner, supervisorProcessId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ports the recorded supervisor, or a descendant of it, listens on.
    /// </summary>
    /// <remarks>
    /// OpenClaw chooses the gateway's port from its own configuration, so this
    /// package observes the result rather than dictating it. Discovering the
    /// port from the process is also the stronger ownership proof: a port this
    /// package supplied only ever confirmed its own assumption.
    /// </remarks>
    public static IReadOnlyList<int> ListeningPortsOwnedBy(int supervisorProcessId)
    {
        List<int> ports = [];
        foreach ((int port, int owner) in TcpListenerOwnership.GetListeners())
        {
            if (!ports.Contains(port) &&
                ProcessAncestry.IsSelfOrDescendant(owner, supervisorProcessId))
            {
                ports.Add(port);
            }
        }

        ports.Sort();
        return ports;
    }

    public static bool AnythingListeningOn(int port)
    {
        if (port <= 0)
        {
            return false;
        }

        return TcpListenerOwnership.GetListenerProcessIds(port).Count > 0;
    }
}
