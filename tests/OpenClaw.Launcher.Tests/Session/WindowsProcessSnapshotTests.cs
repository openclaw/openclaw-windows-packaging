using System.Buffers.Binary;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class WindowsProcessSnapshotTests
{
    [Fact]
    public void ParsesProcessIdsAndNonReusableSequenceNumbers()
    {
        byte[] table = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(table, 48);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(8), 1234);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(24), 17);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(56), 5678);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(72), 0x8000_0000_0000_0001);

        IReadOnlyDictionary<int, ulong> sequences = WindowsProcessSnapshot.Parse(table);

        Assert.Equal((ulong)17, sequences[1234]);
        Assert.Equal(0x8000_0000_0000_0001, sequences[5678]);
    }

    [Theory]
    [InlineData(8, 5678, 18)]
    [InlineData(48, 1234, 18)]
    [InlineData(48, 5678, 0)]
    public void MalformedOrAmbiguousProcessSequenceIsRejected(
        int nextOffset, long secondProcessId, ulong secondSequence)
    {
        byte[] table = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(table, nextOffset);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(8), 1234);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(24), 17);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(56), secondProcessId);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(72), secondSequence);

        Assert.Throws<InvalidDataException>(() => WindowsProcessSnapshot.Parse(table));
    }

    [Fact]
    public void ParsesCreationTimesWithoutOpeningProcessHandles()
    {
        byte[] table = new byte[176];
        var created = new DateTimeOffset(2026, 10, 5, 3, 9, 7, TimeSpan.Zero);
        BinaryPrimitives.WriteInt32LittleEndian(table, 88);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(32), created.ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(80), 4980);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(120), created.AddSeconds(1).ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(168), 5000);

        var times = WindowsProcessSnapshot.ParseCreationTimes(table);

        Assert.Equal(created, times[4980]);
        Assert.Equal(created.AddSeconds(1), times[5000]);
    }

    [Theory]
    [InlineData(8, 5000, 1)]
    [InlineData(88, 4980, 1)]
    [InlineData(88, 5000, 0)]
    public void InvalidCreationTimeEvidenceIsRejected(int next, int secondPid, long created)
    {
        byte[] table = new byte[176];
        BinaryPrimitives.WriteInt32LittleEndian(table, next);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(32), 1);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(80), 4980);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(120), created);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(168), secondPid);

        Assert.Throws<InvalidDataException>(() => WindowsProcessSnapshot.ParseCreationTimes(table));
    }

    [Fact]
    public void WindowsSnapshotMatchesTheCurrentProcessCreationTime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var current = System.Diagnostics.Process.GetCurrentProcess();

        Assert.Equal(current.StartTime.ToUniversalTime(),
            WindowsProcessSnapshot.CaptureCreationTimes()[current.Id]);
    }

    [Fact]
    public void ListenerWithoutSequenceCannotBecomePackageOwnershipProof()
    {
        var result = new SessionInspectResult
        {
            RequestId = "inspect-1",
            ProcessFound = true,
            StartTimeMatches = true,
            PortListening = true,
            ListenerOwned = true,
            OwnedListeners =
            [
                new SessionOwnedListener
                {
                    Port = 19001,
                    ProcessId = 1234,
                    ProcessStartTimeUtc = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)
                }
            ]
        };

        Assert.Throws<SessionLaunchException>(() => SessionInspectProtocol.ReadResult(
            SessionInspectProtocol.SerializeResult(result)));
    }
}
