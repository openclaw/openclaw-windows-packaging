using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Session;

[JsonConverter(typeof(JsonStringEnumConverter<SetupPhase>))]
internal enum SetupPhase
{
    Ready,
    Preparing,
    TearingDown
}

/// <summary>
/// The durable marker that this installation completed explicit setup.
/// </summary>
/// <remarks>
/// The marker is separate from the session record because older management
/// commands can create a session without completing the new setup workflow.
/// A valid session therefore does not, by itself, authorize <c>openclaw</c>.
/// </remarks>
internal sealed record SetupRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("applicationId")]
    public string ApplicationId { get; init; } = string.Empty;

    [JsonPropertyName("completedUtc")]
    public DateTimeOffset CompletedUtc { get; init; }

    [JsonPropertyName("phase")]
    public SetupPhase Phase { get; init; } = SetupPhase.Ready;

    [JsonPropertyName("sandboxId")]
    public string? SandboxId { get; init; }

    [JsonPropertyName("startupEnabled")]
    public bool StartupEnabled { get; init; } = true;
}

/// <summary>Why the explicit setup marker could not be used.</summary>
internal enum SetupStateFault
{
    /// <summary>Setup has not completed for this installation.</summary>
    Missing,

    /// <summary>The marker exists but cannot be read as a record.</summary>
    Unreadable,

    /// <summary>The marker was written by an incompatible newer version.</summary>
    UnsupportedSchema,

    /// <summary>The marker is missing values it cannot be used without.</summary>
    Incomplete,

    /// <summary>The marker belongs to a different package identity.</summary>
    ForeignIdentity,
}

internal sealed record SetupStateResult(
    SetupRecord? Record,
    SetupStateFault? Fault,
    string? Detail)
{
    public static SetupStateResult Found(SetupRecord record) =>
        new(record, null, null);

    public static SetupStateResult Failed(SetupStateFault fault, string detail) =>
        new(null, fault, detail);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SetupRecord))]
internal sealed partial class SetupStateJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and atomically writes the explicit setup marker.
/// </summary>
internal sealed class SetupStateStore
{
    public const int CurrentSchemaVersion = 2;

    private readonly string _filePath;

    public SetupStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public SetupStateResult Read(string expectedApplicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedApplicationId);

        string text;
        try
        {
            text = File.ReadAllText(_filePath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return SetupStateResult.Failed(
                SetupStateFault.Missing,
                "Explicit setup has not completed.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return SetupStateResult.Failed(
                SetupStateFault.Unreadable,
                $"The setup record could not be read: {exception.Message}");
        }

        SetupRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(
                text,
                SetupStateJsonContext.Default.SetupRecord);
        }
        catch (JsonException exception)
        {
            return SetupStateResult.Failed(
                SetupStateFault.Unreadable,
                $"The setup record is not valid JSON: {exception.Message}");
        }

        if (record is null)
        {
            return SetupStateResult.Failed(
                SetupStateFault.Unreadable,
                "The setup record is empty.");
        }

        if (record.SchemaVersion > CurrentSchemaVersion)
        {
            return SetupStateResult.Failed(
                SetupStateFault.UnsupportedSchema,
                $"The setup record uses schema version {record.SchemaVersion}, " +
                $"but this build understands version {CurrentSchemaVersion}. A " +
                "newer OpenClaw installation may have written it.");
        }

        if (record.SchemaVersion < 1 ||
            string.IsNullOrWhiteSpace(record.ApplicationId) ||
            !Enum.IsDefined(record.Phase))
        {
            return SetupStateResult.Failed(
                SetupStateFault.Incomplete,
                "The setup record is missing its schema version or application identity.");
        }

        if (!string.Equals(
                record.ApplicationId,
                expectedApplicationId,
                StringComparison.OrdinalIgnoreCase))
        {
            return SetupStateResult.Failed(
                SetupStateFault.ForeignIdentity,
                $"The setup record belongs to '{record.ApplicationId}', but this " +
                $"installation is '{expectedApplicationId}'.");
        }

        return SetupStateResult.Found(record);
    }

    public void Write(SetupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ApplicationId);

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string text = JsonSerializer.Serialize(
            record with { SchemaVersion = CurrentSchemaVersion },
            SetupStateJsonContext.Default.SetupRecord);

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
}
