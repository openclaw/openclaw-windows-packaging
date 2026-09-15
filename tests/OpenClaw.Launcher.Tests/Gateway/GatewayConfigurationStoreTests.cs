using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayConfigurationStoreTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

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

    private string Path_ => Path.Combine(_root, "gateway-config.json");

    private GatewayConfigurationStore Store => new(Path_);

    private static string? NoEnvironment(string name) => null;

    // An unconfigured installation must not name a port at all. Naming one
    // would override `gateway.port` in OpenClaw's own configuration and move
    // the gateway away from where its clients look.
    [Fact]
    public void AnUnconfiguredInstallationLeavesThePortToOpenClaw()
    {
        GatewayLaunchConfiguration resolved = Store.Resolve(NoEnvironment);

        Assert.Null(resolved.Port);
    }

    [Fact]
    public void AConfiguredPortSurvivesForTheNextSignIn()
    {
        // The logon task starts the gateway with no one watching, so a port
        // chosen interactively has to still be in effect then.
        Store.Write(new GatewayLaunchConfiguration { Port = 9100 });

        Assert.Equal(9100, Store.Resolve(NoEnvironment).Port);
    }

    [Fact]
    public void TheEnvironmentOverridesThePortForOneInvocationOnly()
    {
        Store.Write(new GatewayLaunchConfiguration { Port = 9100 });

        GatewayLaunchConfiguration resolved = Store.Resolve(
            name => name == GatewayConfigurationStore.PortVariable ? "9200" : null);

        Assert.Equal(9200, resolved.Port);

        // The stored configuration is untouched, so the next sign-in still uses
        // what the user actually configured.
        Assert.Equal(9100, Store.Read().Configuration!.Port);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    [InlineData("http")]
    public void AnUnusableOverrideIsRefusedRatherThanIgnored(string value)
    {
        // Falling back to the configured port would start the gateway
        // somewhere the user did not ask for while appearing to honor the
        // override.
        Assert.Throws<GatewayConfigurationException>(
            () => Store.Resolve(
                name => name == GatewayConfigurationStore.PortVariable ? value : null));
    }

    [Fact]
    public void AnUnreadableConfigurationIsAnErrorRatherThanASilentDefault()
    {
        File.WriteAllText(Path_, "{ not json");

        Assert.Throws<GatewayConfigurationException>(
            () => Store.Resolve(NoEnvironment));
    }

    [Fact]
    public void ANewerSchemaIsRefusedRatherThanMisread()
    {
        File.WriteAllText(Path_, """{"schemaVersion":99,"port":9100}""");

        Assert.Equal(
            GatewayConfigurationFault.UnsupportedSchema,
            Store.Read().Fault);
    }

    [Fact]
    public void AnOutOfRangeStoredPortIsReportedAsInvalid()
    {
        File.WriteAllText(Path_, """{"schemaVersion":1,"port":70000}""");

        Assert.Equal(GatewayConfigurationFault.Invalid, Store.Read().Fault);
    }

    [Fact]
    public void AnUnusablePortCannotBeWritten()
    {
        Assert.Throws<GatewayConfigurationException>(
            () => Store.Write(new GatewayLaunchConfiguration { Port = 0 }));
    }

    [Fact]
    public void AMissingConfigurationIsNotAFault()
    {
        GatewayConfigurationResult result = Store.Read();

        Assert.Equal(GatewayConfigurationFault.Missing, result.Fault);
        Assert.Null(result.Detail);
    }

    [Fact]
    public void TheConfigurationRoundTrips()
    {
        Store.Write(new GatewayLaunchConfiguration
        {
            Port = 9100
        });

        GatewayLaunchConfiguration? read = Store.Read().Configuration;

        Assert.NotNull(read);
        Assert.Equal(9100, read.Port);
        Assert.Equal(GatewayConfigurationStore.CurrentSchemaVersion, read.SchemaVersion);
    }
}
