namespace OpenClaw.Launcher.Tests;

public sealed class GatewayIsolationStoreTests : IDisposable
{
    private readonly string _directory = TestDirectory.Create();

    [Fact]
    public void ReadDefaultsToDisabled()
    {
        var store = new GatewayIsolationStore(
            Path.Combine(_directory, "state.json"),
            useExactPath: true);

        Assert.False(store.Read().Enabled);
    }

    [Fact]
    public void WriteRoundTripsRequestedState()
    {
        string path = Path.Combine(_directory, "nested", "state.json");
        var store = new GatewayIsolationStore(path, useExactPath: true);

        store.Write(new GatewayIsolationState(true));

        Assert.True(store.Read().Enabled);
    }

    [Fact]
    public void ReadRejectsMalformedState()
    {
        string path = Path.Combine(_directory, "state.json");
        File.WriteAllText(path, "not-json");
        var store = new GatewayIsolationStore(path, useExactPath: true);

        Assert.Throws<InvalidDataException>(store.Read);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
