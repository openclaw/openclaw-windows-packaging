using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// The durable record of the session this installation owns.
/// </summary>
/// <remarks>
/// <para>
/// The opaque backend id is stored verbatim. It carries the provisioning
/// application identity and backend routing prefix, so it cannot be rebuilt
/// from parts and must never be normalized on the way in or out.
/// </para>
/// <para>
/// Ownership lives here and nowhere else. The backend's deprovision is
/// idempotent and unrelated agent accounts can already exist on a machine, so
/// neither the presence of an account nor the absence of an error proves this
/// installation created anything.
/// </para>
/// </remarks>
internal sealed record SessionRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("sandboxId")]
    public string SandboxId { get; init; } = string.Empty;

    [JsonPropertyName("applicationId")]
    public string ApplicationId { get; init; } = string.Empty;

    [JsonPropertyName("agentUserName")]
    public string? AgentUserName { get; init; }

    [JsonPropertyName("agentUserSid")]
    public string? AgentUserSid { get; init; }

    [JsonPropertyName("workspacePath")]
    public string? WorkspacePath { get; init; }

    [JsonPropertyName("wireVersion")]
    public string? WireVersion { get; init; }

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>
/// Why a stored session record could not be used.
/// </summary>
internal enum SessionStateFault
{
    /// <summary>No record has been written yet.</summary>
    Missing,

    /// <summary>The file exists but is not readable as a record.</summary>
    Unreadable,

    /// <summary>The record was written by an incompatible newer version.</summary>
    UnsupportedSchema,

    /// <summary>The record is missing values it cannot be used without.</summary>
    Incomplete,

    /// <summary>The record belongs to a different package identity.</summary>
    ForeignIdentity,
}

/// <summary>
/// The outcome of reading the stored session record.
/// </summary>
internal sealed record SessionStateResult(
    SessionRecord? Record,
    SessionStateFault? Fault,
    string? Detail)
{
    public bool HasRecord => Record is not null;

    public static SessionStateResult Found(SessionRecord record) =>
        new(record, null, null);

    public static SessionStateResult Failed(SessionStateFault fault, string detail) =>
        new(null, fault, detail);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionRecord))]
internal sealed partial class SessionStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads and atomically writes the owned-session record.
/// </summary>
internal sealed class SessionStateStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _filePath;

    public SessionStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Reads the record, distinguishing "no session" from "unusable session".
    /// </summary>
    /// <remarks>
    /// A damaged record never degrades to <see cref="SessionStateFault.Missing"/>.
    /// Provisioning a replacement over a record we cannot read would abandon a
    /// live backend session and the user's guest profile with it.
    /// </remarks>
    public SessionStateResult Read(string expectedApplicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedApplicationId);

        string text;
        try
        {
            text = File.ReadAllText(_filePath);
        }
        catch (FileNotFoundException)
        {
            return SessionStateResult.Failed(
                SessionStateFault.Missing,
                "No session has been recorded.");
        }
        catch (DirectoryNotFoundException)
        {
            return SessionStateResult.Failed(
                SessionStateFault.Missing,
                "No session has been recorded.");
        }
        catch (IOException exception)
        {
            return SessionStateResult.Failed(
                SessionStateFault.Unreadable,
                $"The session record could not be read: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return SessionStateResult.Failed(
                SessionStateFault.Unreadable,
                $"The session record could not be read: {exception.Message}");
        }

        SessionRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(
                text,
                SessionStateJsonContext.Default.SessionRecord);
        }
        catch (JsonException exception)
        {
            return SessionStateResult.Failed(
                SessionStateFault.Unreadable,
                $"The session record is not valid JSON: {exception.Message}");
        }

        if (record is null)
        {
            return SessionStateResult.Failed(
                SessionStateFault.Unreadable,
                "The session record is empty.");
        }

        if (record.SchemaVersion > CurrentSchemaVersion)
        {
            return SessionStateResult.Failed(
                SessionStateFault.UnsupportedSchema,
                $"The session record uses schema version {record.SchemaVersion}, but " +
                $"this build understands version {CurrentSchemaVersion}. A newer " +
                "OpenClaw installation may have written it.");
        }

        if (record.SchemaVersion < 1)
        {
            return SessionStateResult.Failed(
                SessionStateFault.Incomplete,
                "The session record does not declare a schema version.");
        }

        if (string.IsNullOrWhiteSpace(record.ApplicationId))
        {
            return SessionStateResult.Failed(
                SessionStateFault.Incomplete,
                "The session record does not contain an application identity.");
        }

        // Identity is checked before the sandbox id so a record from another
        // installation is never reported as this installation's corruption.
        if (!string.Equals(
                record.ApplicationId,
                expectedApplicationId,
                StringComparison.OrdinalIgnoreCase))
        {
            return SessionStateResult.Failed(
                SessionStateFault.ForeignIdentity,
                $"The session record belongs to '{record.ApplicationId}', but this " +
                $"installation is '{expectedApplicationId}'.");
        }

        try
        {
            _ = MxcSandboxId.Parse(record.SandboxId);
        }
        catch (MxcException exception)
        {
            return SessionStateResult.Failed(
                SessionStateFault.Incomplete,
                $"The session record does not contain a usable sandbox identifier: " +
                $"{exception.Message}");
        }

        return SessionStateResult.Found(record);
    }

    /// <summary>
    /// Replaces the record atomically.
    /// </summary>
    /// <remarks>
    /// The write lands on a temporary file in the same directory and is then
    /// moved over the target, so a crash mid-write cannot leave a truncated
    /// record that would later read as a damaged session.
    /// </remarks>
    public void Write(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ApplicationId);
        _ = MxcSandboxId.Parse(record.SandboxId);

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string text = JsonSerializer.Serialize(
            record with { SchemaVersion = CurrentSchemaVersion },
            SessionStateJsonContext.Default.SessionRecord);

        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, text);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Removes the record. Deleting nothing is success.
    /// </summary>
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
