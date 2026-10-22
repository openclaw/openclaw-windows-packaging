using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OpenClaw.SessionHost;

// SystemBasicProcessInformation provides a PID-reuse-proof identity without opening
// another account's process handle. It is available on Windows 11 26100.4770+.
internal static class WindowsProcessSnapshot
{
    private const int SystemBasicProcessInformation = 252;
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusInvalidInfoClass = unchecked((int)0xC0000003);
    private const int FirstBufferSize = 256 * 1024;
    private const int MaxBufferSize = 16 * 1024 * 1024;
    private const int MinimumEntrySize = 48;

    public static IReadOnlyDictionary<int, ulong> Capture() =>
        Query(SystemBasicProcessInformation, MinimumEntrySize, bytes => Parse(bytes));

    // SystemProcessInformation exposes creation times even when an agent cannot
    // open a reused PID. Unlike a denied handle, an observed mismatch proves reuse.
    public static IReadOnlyDictionary<int, DateTimeOffset> CaptureCreationTimes() =>
        Query(SystemProcessInformation, 88, bytes => ParseCreationTimes(bytes));

    private static T Query<T>(int informationClass, int minimumEntrySize, Func<byte[], T> parse)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            throw new NotSupportedException("Gateway process inspection requires 64-bit Windows.");
        }

        for (int capacity = FirstBufferSize; capacity <= MaxBufferSize;)
        {
            IntPtr buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                int status = NtQuerySystemInformation(
                    informationClass, buffer, capacity, out int returned);
                if (status == 0)
                {
                    if (returned < minimumEntrySize || returned > MaxBufferSize || returned > capacity)
                    {
                        throw new InvalidDataException("Windows returned an invalid process table size.");
                    }
                    byte[] bytes = new byte[returned];
                    Marshal.Copy(buffer, bytes, 0, returned);
                    return parse(bytes);
                }
                if (status == StatusInvalidInfoClass)
                {
                    throw new NotSupportedException(
                        "Windows does not support this process inspection. Update Windows.");
                }
                if (status != StatusInfoLengthMismatch)
                {
                    throw new InvalidOperationException(
                        $"Windows could not inspect process identities (NTSTATUS 0x{status:X8}).");
                }
                capacity = Math.Max(capacity * 2, returned);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidDataException("The Windows process table exceeds the bounded inspection size.");
    }

    internal static IReadOnlyDictionary<int, ulong> Parse(ReadOnlySpan<byte> bytes)
    {
        var sequences = new Dictionary<int, ulong>();
        int offset = 0;
        while (true)
        {
            if (offset > bytes.Length - MinimumEntrySize)
            {
                throw new InvalidDataException("The Windows process sequence table is truncated.");
            }

            ReadOnlySpan<byte> entry = bytes[offset..];
            int next = BinaryPrimitives.ReadInt32LittleEndian(entry);
            long processId = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
            ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]);
            if (processId is > 0 and <= int.MaxValue &&
                (sequence == 0 || !sequences.TryAdd((int)processId, sequence)))
            {
                throw new InvalidDataException("The Windows process sequence table has an invalid identity.");
            }

            if (next == 0)
            {
                return sequences;
            }
            if (next < MinimumEntrySize || next % 8 != 0 || next > bytes.Length - offset)
            {
                throw new InvalidDataException("The Windows process sequence table has an invalid entry offset.");
            }
            offset += next;
        }
    }

    internal static IReadOnlyDictionary<int, DateTimeOffset> ParseCreationTimes(ReadOnlySpan<byte> bytes)
    {
        var times = new Dictionary<int, DateTimeOffset>();
        int offset = 0;
        while (true)
        {
            if (offset > bytes.Length - 88)
            {
                throw new InvalidDataException("The Windows process creation-time table is truncated.");
            }
            ReadOnlySpan<byte> entry = bytes[offset..];
            int next = BinaryPrimitives.ReadInt32LittleEndian(entry);
            long processId = BinaryPrimitives.ReadInt64LittleEndian(entry[80..]);
            long created = BinaryPrimitives.ReadInt64LittleEndian(entry[32..]);
            if (processId is > 0 and <= int.MaxValue)
            {
                if (created <= 0 || !times.TryAdd((int)processId, DateTimeOffset.FromFileTime(created)))
                {
                    throw new InvalidDataException("The Windows process creation-time table has an invalid identity.");
                }
            }
            if (next == 0)
            {
                return times;
            }
            if (next < 88 || next % 8 != 0 || next > bytes.Length - offset)
            {
                throw new InvalidDataException("The Windows process creation-time table has an invalid entry offset.");
            }
            offset += next;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int informationClass, IntPtr buffer, int length, out int returnedLength);
}
