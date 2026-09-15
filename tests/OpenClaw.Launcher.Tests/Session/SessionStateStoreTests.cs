using System.Text.Json;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionStateStoreTests : IDisposable
{
    private const string ApplicationId = "PFN:OpenClaw.Gateway_abc123";
    private const string SandboxId = "iso:AAAAbbbbCCCC";

    private readonly string _root = TestDirectory.Create();

    private string StatePath => Path.Combine(_root, "session.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static SessionRecord Record() => new()
    {
        SandboxId = SandboxId,
        ApplicationId = ApplicationId,
        AgentUserName = "agent_1",
        AgentUserSid = "S-1-5-21-0-0-0-1001",
        WorkspacePath = @"C:\Users\agent_1\Shared",
        WireVersion = "0.6.0-alpha",
        CreatedUtc = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
    };

    [Fact]
    public void MissingRecordIsReportedAsMissing()
    {
        var store = new SessionStateStore(StatePath);

        SessionStateResult result = store.Read(ApplicationId);

        Assert.False(result.HasRecord);
        Assert.Equal(SessionStateFault.Missing, result.Fault);
    }

    [Fact]
    public void MissingDirectoryIsMissingRatherThanUnreadable()
    {
        var store = new SessionStateStore(
            Path.Combine(_root, "no-such-dir", "session.json"));

        Assert.Equal(SessionStateFault.Missing, store.Read(ApplicationId).Fault);
    }

    [Fact]
    public void WrittenRecordRoundTripsEveryPersistedField()
    {
        var store = new SessionStateStore(StatePath);
        SessionRecord written = Record();

        store.Write(written);
        SessionStateResult result = store.Read(ApplicationId);

        SessionRecord read = Assert.IsType<SessionRecord>(result.Record);
        Assert.Equal(SessionStateStore.CurrentSchemaVersion, read.SchemaVersion);
        Assert.Equal(written.SandboxId, read.SandboxId);
        Assert.Equal(written.ApplicationId, read.ApplicationId);
        Assert.Equal(written.AgentUserName, read.AgentUserName);
        Assert.Equal(written.AgentUserSid, read.AgentUserSid);
        Assert.Equal(written.WorkspacePath, read.WorkspacePath);
        Assert.Equal(written.WireVersion, read.WireVersion);
        Assert.Equal(written.CreatedUtc, read.CreatedUtc);
    }

    [Fact]
    public void OpaqueSandboxIdIsPreservedVerbatim()
    {
        const string opaque = "iso:eyJhIjoiYiJ9-_==";
        var store = new SessionStateStore(StatePath);

        store.Write(Record() with { SandboxId = opaque });

        Assert.Equal(opaque, store.Read(ApplicationId).Record!.SandboxId);
    }

    [Fact]
    public void WriteStampsTheCurrentSchemaVersion()
    {
        var store = new SessionStateStore(StatePath);

        store.Write(Record() with { SchemaVersion = 0 });

        Assert.Equal(
            SessionStateStore.CurrentSchemaVersion,
            store.Read(ApplicationId).Record!.SchemaVersion);
    }

    [Fact]
    public void WriteCreatesMissingDirectories()
    {
        var store = new SessionStateStore(
            Path.Combine(_root, "deep", "deeper", "session.json"));

        store.Write(Record());

        Assert.True(store.Read(ApplicationId).HasRecord);
    }

    [Fact]
    public void WriteLeavesNoTemporaryFileBehind()
    {
        var store = new SessionStateStore(StatePath);

        store.Write(Record());

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void WriteReplacesAnExistingRecord()
    {
        var store = new SessionStateStore(StatePath);
        store.Write(Record());

        store.Write(Record() with { SandboxId = "iso:second", AgentUserName = "agent_2" });

        SessionRecord read = store.Read(ApplicationId).Record!;
        Assert.Equal("iso:second", read.SandboxId);
        Assert.Equal("agent_2", read.AgentUserName);
    }

    [Fact]
    public void CorruptJsonIsUnreadableRatherThanMissing()
    {
        File.WriteAllText(StatePath, "{ not json");
        var store = new SessionStateStore(StatePath);

        SessionStateResult result = store.Read(ApplicationId);

        Assert.Equal(SessionStateFault.Unreadable, result.Fault);
        Assert.False(result.HasRecord);
    }

    [Fact]
    public void EmptyJsonDocumentIsUnreadable()
    {
        File.WriteAllText(StatePath, "null");
        var store = new SessionStateStore(StatePath);

        Assert.Equal(SessionStateFault.Unreadable, store.Read(ApplicationId).Fault);
    }

    [Fact]
    public void NewerSchemaIsRejectedInsteadOfPartiallyRead()
    {
        var store = new SessionStateStore(StatePath);
        store.Write(Record());
        string text = File.ReadAllText(StatePath)
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal);
        File.WriteAllText(StatePath, text);

        SessionStateResult result = store.Read(ApplicationId);

        Assert.Equal(SessionStateFault.UnsupportedSchema, result.Fault);
        Assert.Contains("99", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordWithoutSchemaVersionIsIncomplete()
    {
        File.WriteAllText(
            StatePath,
            $$"""{"sandboxId":"{{SandboxId}}","applicationId":"{{ApplicationId}}"}""");
        var store = new SessionStateStore(StatePath);

        Assert.Equal(SessionStateFault.Incomplete, store.Read(ApplicationId).Fault);
    }

    [Fact]
    public void RecordWithoutApplicationIdIsIncomplete()
    {
        File.WriteAllText(
            StatePath,
            $$"""{"schemaVersion":1,"sandboxId":"{{SandboxId}}"}""");
        var store = new SessionStateStore(StatePath);

        Assert.Equal(SessionStateFault.Incomplete, store.Read(ApplicationId).Fault);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-prefix")]
    public void RecordWithoutAUsableSandboxIdIsIncomplete(string sandboxId)
    {
        File.WriteAllText(
            StatePath,
            $$"""{"schemaVersion":1,"sandboxId":"{{sandboxId}}","applicationId":"{{ApplicationId}}"}""");
        var store = new SessionStateStore(StatePath);

        Assert.Equal(SessionStateFault.Incomplete, store.Read(ApplicationId).Fault);
    }

    [Fact]
    public void RecordFromAnotherInstallationIsForeignRatherThanMissing()
    {
        var store = new SessionStateStore(StatePath);
        store.Write(Record() with { ApplicationId = "PFN:Internal.Gateway_xyz" });

        SessionStateResult result = store.Read(ApplicationId);

        Assert.Equal(SessionStateFault.ForeignIdentity, result.Fault);
        Assert.Contains("Internal.Gateway_xyz", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationIdComparisonIgnoresCase()
    {
        var store = new SessionStateStore(StatePath);
        store.Write(Record());

        Assert.True(store.Read(ApplicationId.ToUpperInvariant()).HasRecord);
    }

    [Fact]
    public void WriteRejectsAnUnusableSandboxIdBeforeTouchingDisk()
    {
        var store = new SessionStateStore(StatePath);

        Assert.ThrowsAny<Exception>(
            () => store.Write(Record() with { SandboxId = "no-prefix" }));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void ClearRemovesTheRecord()
    {
        var store = new SessionStateStore(StatePath);
        store.Write(Record());

        store.Clear();

        Assert.Equal(SessionStateFault.Missing, store.Read(ApplicationId).Fault);
    }

    [Fact]
    public void ClearWithoutARecordSucceeds()
    {
        var store = new SessionStateStore(
            Path.Combine(_root, "absent", "session.json"));

        store.Clear();
    }

    [Fact]
    public void PersistedFileIsReadableJson()
    {
        var store = new SessionStateStore(StatePath);
        store.Write(Record());

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(StatePath));

        Assert.Equal(
            SandboxId,
            document.RootElement.GetProperty("sandboxId").GetString());
    }
}
