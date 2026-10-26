using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.StateTransfer;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.StateTransfer;

public sealed class StateArchiveStoreTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationIsPrivateBeforeFirstByteUnderABroadlyReadableParent(bool protection)
    {
        var directory = new DirectoryInfo(_root);
        DirectorySecurity permissions = directory.GetAccessControl();
        permissions.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(permissions);
        string parentSecurity = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        byte[] data = "synthetic private archive"u8.ToArray();
        var store = new StateArchiveStore(_root, _log.Add);
        bool inspectedBeforeRead = false;
        using var source = new InspectingStream(data, () =>
        {
            string parent = protection ? Path.Combine(_root, "before-restore") : _root;
            string temporary = Assert.Single(Directory.GetFiles(parent, ".archive-*.tmp"));
            AssertPrivateFile(temporary);
            Assert.Equal(0, new FileInfo(temporary).Length);
            inspectedBeforeRead = true;
        });

        string retained = await store.PublishAsync(
            source, Metadata(data), null, protection, CancellationToken.None);

        Assert.True(inspectedBeforeRead);
        AssertPrivateFile(retained);
        Assert.Equal(data, await File.ReadAllBytesAsync(retained));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(retained)!, ".archive-*.tmp"));
        Assert.Equal(parentSecurity,
            directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    [Fact]
    public async Task DeniedPrivateCreationCannotPublishOrFallBackToAnotherLocation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var user = Assert.IsType<SecurityIdentifier>(identity.User);
        var directory = new DirectoryInfo(_root);
        DirectorySecurity original = directory.GetAccessControl();
        DirectorySecurity denied = directory.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.CreateFiles, AccessControlType.Deny));
        directory.SetAccessControl(denied);
        try
        {
            byte[] data = "synthetic private archive"u8.ToArray();
            using var source = new MemoryStream(data);
            var store = new StateArchiveStore(_root, _log.Add);

            await Assert.ThrowsAsync<IOException>(() => store.PublishAsync(
                source, Metadata(data), Path.Combine(_root, "refused.tar.gz"),
                protection: false, CancellationToken.None));

            Assert.Empty(Directory.GetFiles(_root));
        }
        finally
        {
            directory.SetAccessControl(original);
        }
    }

    internal static void AssertPrivateFile(string path, bool protectedAcl = true, string? participantSid = null)
    {
        FileSecurity permissions = new FileInfo(path).GetAccessControl();
        if (protectedAcl)
        {
            Assert.True(permissions.AreAccessRulesProtected);
        }
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var user = Assert.IsType<SecurityIdentifier>(identity.User);
        HashSet<SecurityIdentifier> expected =
        [
            user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        ];
        if (participantSid is not null)
        {
            expected.Add(new SecurityIdentifier(participantSid));
        }
        FileSystemAccessRule[] rules = [.. permissions.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];
        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Contains(Assert.IsType<SecurityIdentifier>(rule.IdentityReference), expected);
        });
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(user));
    }

    private sealed class InspectingStream(byte[] bytes, Action beforeRead) : MemoryStream(bytes)
    {
        private bool _inspected;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_inspected)
            {
                beforeRead();
                _inspected = true;
            }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

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
