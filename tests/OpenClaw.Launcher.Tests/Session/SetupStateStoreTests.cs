using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SetupStateStoreTests : IDisposable
{
    private const string ApplicationId = "PFN:OpenClaw.Gateway_test";
    private readonly string _root = TestDirectory.Create();

    private string StatePath => Path.Combine(_root, "setup.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void MissingMarkerIsDistinctFromAnUnreadableOne()
    {
        SetupStateStore store = new(StatePath);

        Assert.Equal(SetupStateFault.Missing, store.Read(ApplicationId).Fault);

        File.WriteAllText(StatePath, "{ not json");

        Assert.Equal(SetupStateFault.Unreadable, store.Read(ApplicationId).Fault);
    }

    [Fact]
    public void MarkerRoundTripsWithItsApplicationIdentity()
    {
        SetupStateStore store = new(StatePath);
        DateTimeOffset completed = new(2026, 9, 11, 18, 0, 0, TimeSpan.Zero);

        store.Write(new SetupRecord
        {
            ApplicationId = ApplicationId,
            CompletedUtc = completed
        });

        SetupRecord? record = store.Read(ApplicationId).Record;

        Assert.NotNull(record);
        Assert.Equal(ApplicationId, record.ApplicationId);
        Assert.Equal(completed, record.CompletedUtc);
        Assert.Equal(SetupStateStore.CurrentSchemaVersion, record.SchemaVersion);
    }

    [Fact]
    public void AMarkerForAnotherPackageCannotBeAdopted()
    {
        SetupStateStore store = new(StatePath);
        store.Write(new SetupRecord { ApplicationId = "PFN:Other.Package_test" });

        SetupStateResult result = store.Read(ApplicationId);

        Assert.Equal(SetupStateFault.ForeignIdentity, result.Fault);
        Assert.Null(result.Record);
    }

    [Fact]
    public void ANewerMarkerIsRefusedRatherThanMisread()
    {
        File.WriteAllText(
            StatePath,
            """{"schemaVersion":99,"applicationId":"PFN:OpenClaw.Gateway_test"}""");

        Assert.Equal(
            SetupStateFault.UnsupportedSchema,
            new SetupStateStore(StatePath).Read(ApplicationId).Fault);
    }

    [Fact]
    public void ClearingTheMarkerIsIdempotent()
    {
        SetupStateStore store = new(StatePath);

        store.Clear();
        Assert.Equal(SetupStateFault.Missing, store.Read(ApplicationId).Fault);

        store.Write(new SetupRecord { ApplicationId = ApplicationId });
        store.Clear();

        Assert.Equal(SetupStateFault.Missing, store.Read(ApplicationId).Fault);
    }
}
