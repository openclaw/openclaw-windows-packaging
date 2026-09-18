using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Gateway;

[JsonConverter(typeof(JsonStringEnumConverter<GatewayGuidanceAcknowledgement>))]
internal enum GatewayGuidanceAcknowledgement
{
    ManualStartInvoked,
    GatewayObservedRunning
}

internal sealed record GatewayGuidanceRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("logonSessionId")]
    public string LogonSessionId { get; init; } = string.Empty;

    [JsonPropertyName("acknowledgement")]
    public GatewayGuidanceAcknowledgement Acknowledgement { get; init; }

    [JsonPropertyName("acknowledgedUtc")]
    public DateTimeOffset AcknowledgedUtc { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GatewayGuidanceRecord))]
internal sealed partial class GatewayGuidanceJsonContext : JsonSerializerContext;

internal sealed class GatewayGuidanceStateStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _filePath;

    public GatewayGuidanceStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public bool IsAcknowledged(string logonSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logonSessionId);

        try
        {
            GatewayGuidanceRecord? record = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath),
                GatewayGuidanceJsonContext.Default.GatewayGuidanceRecord);
            return record is not null &&
                record.SchemaVersion == CurrentSchemaVersion &&
                Enum.IsDefined(record.Acknowledgement) &&
                string.Equals(
                    record.LogonSessionId,
                    logonSessionId,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or
            IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public void Write(
        string logonSessionId,
        GatewayGuidanceAcknowledgement acknowledgement,
        DateTimeOffset acknowledgedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logonSessionId);
        if (!Enum.IsDefined(acknowledgement))
        {
            throw new ArgumentOutOfRangeException(nameof(acknowledgement));
        }

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string text = JsonSerializer.Serialize(
            new GatewayGuidanceRecord
            {
                SchemaVersion = CurrentSchemaVersion,
                LogonSessionId = logonSessionId,
                Acknowledgement = acknowledgement,
                AcknowledgedUtc = acknowledgedUtc
            },
            GatewayGuidanceJsonContext.Default.GatewayGuidanceRecord);
        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, text);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
