using System.Text.Json;
using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayToolsBrokerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public async Task RegisterAndListReturnOnlyBoundedStatus()
    {
        string executable = Path.Combine(_root, "selected-tool.exe");
        File.WriteAllText(executable, "fixture");
        GatewayToolsBrokerService service = CreateService();

        BrokerResponse registration = await DispatchAsync(service, "registerTool", new
        {
            command = "tool",
            source = "desktop",
            selectedExecutablePath = executable
        });
        BrokerResponse listed = await DispatchAsync(service, "listTools", null);

        Assert.Null(registration.Error);
        Assert.Null(listed.Error);
        string json = JsonSerializer.Serialize(listed, GatewayToolsBrokerJson.Options);
        Assert.Contains("toolreg_", json, StringComparison.Ordinal);
        Assert.DoesNotContain(executable, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegistrationRequiresExplicitDesktopExecutable()
    {
        BrokerResponse response = await DispatchAsync(CreateService(), "registerTool", new
        {
            command = "tool",
            source = "desktop"
        });

        Assert.NotNull(response.Error);
        Assert.DoesNotContain("path", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsMalformedRegistrationIdsWithoutTouchingRegistry()
    {
        BrokerResponse response = await DispatchAsync(CreateService(), "unregisterTool", new
        {
            registrationId = "not-an-id"
        });

        Assert.NotNull(response.Error);
        BrokerResponse listed = await DispatchAsync(CreateService(), "listTools", null);
        Assert.DoesNotContain("toolreg_", JsonSerializer.Serialize(listed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanRegistersOnlyTopLevelSafeExecutables()
    {
        GatewayToolsBrokerService service = CreateService();
        string directory = Path.Combine(_root, "gateway-tools");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tool.exe"), "fixture");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "nested", "other.exe"), "fixture");

        BrokerResponse scanned = await DispatchAsync(service, "scanGatewayTools", null);
        BrokerResponse listed = await DispatchAsync(service, "listTools", null);

        Assert.Null(scanned.Error);
        Assert.Contains("tool", JsonSerializer.Serialize(listed), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other", JsonSerializer.Serialize(listed), StringComparison.OrdinalIgnoreCase);
    }

    private GatewayToolsBrokerService CreateService() => new(
        new GatewayToolRegistry(Path.Combine(_root, "state", "tools.json")),
        Path.Combine(_root, "gateway-tools"),
        () => Path.Combine(_root, "workspace"));

    private static async Task<BrokerResponse> DispatchAsync(
        GatewayToolsBrokerService service,
        string operation,
        object? payload)
    {
        JsonElement element = JsonSerializer.SerializeToElement(payload, GatewayToolsBrokerJson.Options);
        return await service.DispatchAsync(new BrokerRequest(operation, element), CancellationToken.None)
            .ConfigureAwait(false);
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
