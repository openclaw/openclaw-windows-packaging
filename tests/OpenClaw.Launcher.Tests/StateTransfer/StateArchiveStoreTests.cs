using System.Security.Cryptography;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.StateTransfer;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.StateTransfer;

public sealed class StateArchiveStoreTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task PublicationRetainsExactVerifiedBytesAndNeverOverwrites()
    {
        byte[] data = "synthetic archive containing credential bytes"u8.ToArray();
        var store = new StateArchiveStore(_root, _log.Add);
        StateTransferArchive archive = Metadata(data);
        using var source = new MemoryStream(data);

        string retained = await store.PublishAsync(
            source, archive, null, protection: false, CancellationToken.None);
        Assert.Equal(data, await File.ReadAllBytesAsync(retained));
        Assert.Equal(retained, store.Select(null));
        Assert.Equal(data.Length, Assert.Single(store.List()).Length);

        using var collision = new MemoryStream("different bytes"u8.ToArray());
        await Assert.ThrowsAsync<SessionException>(() => store.PublishAsync(
            collision, archive, retained, protection: false, CancellationToken.None));
        Assert.Equal(data, await File.ReadAllBytesAsync(retained));
        Assert.Empty(_log);
    }

    [Fact]
    public async Task ChangedOrCancelledInputCannotPublishOrLeaveStaging()
    {
        byte[] data = "verified"u8.ToArray();
        var store = new StateArchiveStore(_root, _log.Add);
        using var changed = new MemoryStream("modified"u8.ToArray());
        await Assert.ThrowsAsync<SessionException>(() => store.PublishAsync(
            changed, Metadata(data), null, protection: false, CancellationToken.None));

        using var cancelled = new MemoryStream(data);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishAsync(
            cancelled, Metadata(data), null, protection: false, cancellation.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task ProtectionIsListedButCannotBecomeTheDefaultRestoreInput()
    {
        byte[] data = "verified"u8.ToArray();
        var store = new StateArchiveStore(_root, _log.Add);
        using var source = new MemoryStream(data);
        string retained = await store.PublishAsync(
            source, Metadata(data), null, protection: true, CancellationToken.None);
        StateArchiveEntry entry = Assert.Single(store.List());
        Assert.Equal(retained, entry.Path);
        Assert.True(entry.Protection);
        Assert.Throws<SessionException>(() => store.Select(null));
    }

    [Fact]
    public async Task InvalidDestinationFailsBeforeAnySessionWork()
    {
        var coordinator = new StateTransferCoordinator(
            new HostOptions(null, null, []),
            () => throw new InvalidOperationException("A bad destination must not start the agent."),
            new StateArchiveStore(_root, _log.Add),
            _log.Add);

        StateArchiveCommandResult result = await coordinator.BackupAsync(
            new BackupOptions(Path.Combine(_root, "wrong.zip"), false, false),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(".tar.gz", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_root));
    }

    internal static StateTransferArchive Metadata(byte[] data, string? path = null) =>
        new(path ?? @"C:\fixture\backup.tar.gz",
            data.Length, Convert.ToHexString(SHA256.HashData(data)), Verified: true);
}
