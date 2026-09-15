using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// How the gateway should be launched.
/// </summary>
/// <remarks>
/// Persisted rather than derived per launch, because the logon task starts the
/// gateway with no one watching. A port chosen interactively must be the same
/// port used at the next sign-in, or the user's clients would silently fail to
/// find it.
/// </remarks>
internal sealed record GatewayLaunchConfiguration
{
    /// <summary>
    /// OpenClaw's own default gateway port, reported so a user knows where to
    /// look when nothing was configured.
    /// </summary>
    /// <remarks>
    /// Never passed on the command line. It is recorded here only to describe
    /// what OpenClaw will choose; passing it would override the user's own
    /// <c>gateway.port</c> setting, and a constant in this package would
    /// silently diverge the moment upstream changed its default.
    /// </remarks>
    public const int UpstreamDefaultPort = 18789;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    /// <summary>
    /// The port to launch on, or null to let OpenClaw resolve its own.
    /// </summary>
    /// <remarks>
    /// Absent by default and absent from the launch command line when absent
    /// here. The gateway's port belongs to OpenClaw's configuration, exactly
    /// like every other OpenClaw-owned argument this package forwards rather
    /// than invents.
    /// </remarks>
    [JsonPropertyName("port")]
    public int? Port { get; init; }

    /// <summary>
    /// The guest-visible directory the gateway runs in. Stated rather than
    /// inherited: at logon a task's directory is the system directory, and an
    /// interactive caller's directory would make the gateway's behavior depend
    /// on where it happened to be started from.
    /// </summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }

}

/// <summary>Why a stored launch configuration could not be used.</summary>
internal enum GatewayConfigurationFault
{
    /// <summary>Nothing has been configured; defaults apply.</summary>
    Missing,

    /// <summary>The file exists but is not readable as a configuration.</summary>
    Unreadable,

    /// <summary>Written by an incompatible newer version.</summary>
    UnsupportedSchema,

    /// <summary>A stored value is outside what Windows or OpenClaw accepts.</summary>
    Invalid,
}

internal sealed record GatewayConfigurationResult(
    GatewayLaunchConfiguration? Configuration,
    GatewayConfigurationFault? Fault,
    string? Detail);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GatewayLaunchConfiguration))]
internal sealed partial class GatewayConfigurationJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes the gateway's launch configuration.
/// </summary>
internal sealed class GatewayConfigurationStore
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Overrides the configured port for one invocation, for diagnosing a port
    /// conflict without changing what the next sign-in will use.
    /// </summary>
    public const string PortVariable = "OPENCLAW_GATEWAY_PORT";

    private readonly string _filePath;

    public GatewayConfigurationStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>
    /// Returns the configuration to launch with, applying a one-invocation
    /// environment override if present.
    /// </summary>
    /// <remarks>
    /// A configuration that cannot be read is an error rather than a silent
    /// fallback to defaults: starting on a different port than the user
    /// configured would leave their clients unable to find the gateway, with
    /// nothing explaining why.
    /// </remarks>
    public GatewayLaunchConfiguration Resolve(
        string defaultWorkingDirectory,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultWorkingDirectory);

        GatewayConfigurationResult result = Read();
        if (result.Configuration is null &&
            result.Fault != GatewayConfigurationFault.Missing)
        {
            throw new GatewayConfigurationException(
                $"The gateway configuration could not be used: {result.Detail}");
        }

        GatewayLaunchConfiguration configuration =
            result.Configuration ?? new GatewayLaunchConfiguration();

        int? port = ReadPortOverride(
            readEnvironmentVariable ?? Environment.GetEnvironmentVariable)
            ?? configuration.Port;

        return configuration with
        {
            Port = port,
            WorkingDirectory = string.IsNullOrWhiteSpace(configuration.WorkingDirectory)
                ? defaultWorkingDirectory
                : configuration.WorkingDirectory
        };
    }

    public GatewayLaunchConfiguration Resolve(
        string workspacePath,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        GatewayLaunchConfiguration configuration = Resolve(readEnvironmentVariable);
        return configuration with
        {
            WorkingDirectory = configuration.WorkingDirectory ?? workspacePath,
        };
    }

    public GatewayConfigurationResult Read()
    {
        string text;
        try
        {
            text = File.ReadAllText(_filePath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new GatewayConfigurationResult(
                null,
                GatewayConfigurationFault.Missing,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new GatewayConfigurationResult(
                null,
                GatewayConfigurationFault.Unreadable,
                $"The gateway configuration could not be read: {exception.Message}");
        }

        GatewayLaunchConfiguration? configuration;
        try
        {
            configuration = JsonSerializer.Deserialize(
                text,
                GatewayConfigurationJsonContext.Default.GatewayLaunchConfiguration);
        }
        catch (JsonException exception)
        {
            return new GatewayConfigurationResult(
                null,
                GatewayConfigurationFault.Unreadable,
                $"The gateway configuration is not valid JSON: {exception.Message}");
        }

        if (configuration is null)
        {
            return new GatewayConfigurationResult(
                null,
                GatewayConfigurationFault.Unreadable,
                "The gateway configuration is empty.");
        }

        if (configuration.SchemaVersion > CurrentSchemaVersion)
        {
            return new GatewayConfigurationResult(
                null,
                GatewayConfigurationFault.UnsupportedSchema,
                $"The gateway configuration uses schema version " +
                $"{configuration.SchemaVersion}, which is newer than this " +
                $"installation supports ({CurrentSchemaVersion}).");
        }

        if (configuration.Port is int configured && !IsUsablePort(configured))
        {
            return new GatewayConfigurationResult(
                null,
                GatewayConfigurationFault.Invalid,
                $"The configured port {configured} is not a usable " +
                "TCP port (1-65535).");
        }

        return new GatewayConfigurationResult(configuration, null, null);
    }

    public void Write(GatewayLaunchConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Port is int port && !IsUsablePort(port))
        {
            throw new GatewayConfigurationException(
                $"The port {port} is not a usable TCP port (1-65535).");
        }

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string text = JsonSerializer.Serialize(
            configuration with { SchemaVersion = CurrentSchemaVersion },
            GatewayConfigurationJsonContext.Default.GatewayLaunchConfiguration);

        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, text);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            File.Delete(_filePath);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static int? ReadPortOverride(Func<string, string?> readEnvironmentVariable)
    {
        string? value = readEnvironmentVariable(PortVariable)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // An unusable value is refused rather than ignored. Falling back to the
        // configured port would start the gateway somewhere the user did not
        // ask for while appearing to honor the override.
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
            !IsUsablePort(port))
        {
            throw new GatewayConfigurationException(
                $"{PortVariable} is set to '{value}', which is not a usable " +
                "TCP port (1-65535).");
        }

        return port;
    }

    private static bool IsUsablePort(int port) => port is > 0 and <= 65535;
}

/// <summary>
/// The gateway's launch configuration could not be used.
/// </summary>
internal sealed class GatewayConfigurationException : Exception
{
    public GatewayConfigurationException()
    {
    }

    public GatewayConfigurationException(string message)
        : base(message)
    {
    }

    public GatewayConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
