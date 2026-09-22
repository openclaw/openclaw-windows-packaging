using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayToolRegistryTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public void RegisterAndPrepareRuntimeCreatesOnlyEnabledGatewayShims()
    {
        string executable = Path.Combine(_root, "gog.exe");
        File.WriteAllText(executable, "fixture");
        var registry = new GatewayToolRegistry(Path.Combine(_root, "state", "tools.json"));

        GatewayToolStatus registered = registry.Register(
            "gog",
            executable,
            GatewayToolSource.Desktop,
            "Google Workspace CLI");
        registry.SetEnabled("gog", enabled: false);
        GatewayToolRuntime disabled = registry.PrepareRuntime(Path.Combine(_root, "workspace"));

        Assert.Equal("gog", registered.Command);
        Assert.Single(disabled.Tools);
        Assert.False(File.Exists(Path.Combine(disabled.ShimDirectory, "gog.cmd")));

        registry.SetEnabled("gog", enabled: true);
        GatewayToolRuntime enabled = registry.PrepareRuntime(Path.Combine(_root, "workspace"));
        string shim = Path.Combine(enabled.ShimDirectory, "gog.cmd");

        Assert.True(File.Exists(shim));
        Assert.Contains(
            $"\"{Path.GetFullPath(executable)}\" %*",
            File.ReadAllText(shim),
            StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(enabled.ProfileRoot, registered.RuntimeProfileId)));
    }

    [Theory]
    [InlineData("openclaw")]
    [InlineData("bad command")]
    [InlineData("../../tool")]
    public void RegisterRejectsUnsafeOrReservedCommands(string command)
    {
        string executable = Path.Combine(_root, "tool.exe");
        File.WriteAllText(executable, "fixture");
        var registry = new GatewayToolRegistry(Path.Combine(_root, "tools.json"));

        Assert.Throws<ArgumentException>(() => registry.Register(
            command,
            executable,
            GatewayToolSource.Desktop));
    }

    [Fact]
    public void ListDoesNotExposeSelectedExecutablePath()
    {
        string executable = Path.Combine(_root, "tool.exe");
        File.WriteAllText(executable, "fixture");
        var registry = new GatewayToolRegistry(Path.Combine(_root, "tools.json"));
        registry.Register("tool", executable, GatewayToolSource.GatewayTools);

        GatewayToolStatus result = Assert.Single(registry.List());

        Assert.Equal("tool", result.Command);
        Assert.DoesNotContain(executable, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
