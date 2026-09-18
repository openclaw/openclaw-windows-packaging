using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayGuidanceStateStoreTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    private string StatePath => Path.Combine(_root, "gateway-guidance.json");

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MissingMalformedAndOtherLogonStateDoNotAcknowledge()
    {
        var store = new GatewayGuidanceStateStore(StatePath);
        Assert.False(store.IsAcknowledged("logon-a"));

        File.WriteAllText(StatePath, "{ invalid");
        Assert.False(store.IsAcknowledged("logon-a"));

        store.Write(
            "logon-b",
            GatewayGuidanceAcknowledgement.ManualStartInvoked,
            DateTimeOffset.UtcNow);
        Assert.False(store.IsAcknowledged("logon-a"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CurrentLogonAcknowledgementRoundTripsAtomically(
        int acknowledgementValue)
    {
        GatewayGuidanceAcknowledgement acknowledgement =
            (GatewayGuidanceAcknowledgement)acknowledgementValue;
        var store = new GatewayGuidanceStateStore(StatePath);

        store.Write(
            "logon-a",
            acknowledgement,
            new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero));

        Assert.True(store.IsAcknowledged("logon-a"));
        Assert.False(File.Exists(StatePath + ".tmp"));
    }
}
