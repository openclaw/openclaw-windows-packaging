using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// The durable record of the gateway this installation started.
/// </summary>
/// <remarks>
/// <para>
/// The record is evidence of what was started, never evidence that it is still
/// running. Stopping the session terminates detached work without notifying
/// anything, so a record routinely outlives the process it describes and
/// liveness must be re-established inside the session every time it is claimed.
/// </para>
/// <para>
/// The creation time is stored with the identifier because Windows reuses
/// process identifiers. Acting on the identifier alone would eventually claim,
/// and could be asked to stop, an unrelated process.
/// </para>
/// </remarks>
internal sealed record GatewayRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    /// <summary>The session the gateway was started in.</summary>
    [JsonPropertyName("sandboxId")]
    public string SandboxId { get; init; } = string.Empty;

    [JsonPropertyName("processId")]
    public int ProcessId { get; init; }

    [JsonPropertyName("launchPending")]
    public bool LaunchPending { get; init; }

    [JsonPropertyName("processStartTimeUtc")]
    public DateTimeOffset ProcessStartTimeUtc { get; init; }

    [JsonPropertyName("helperPath")]
    public string? HelperPath { get; init; }

    /// <summary>
    /// The port this launch pinned, or null when OpenClaw chose it.
    /// </summary>
    /// <remarks>
    /// Recorded rather than inferred, so a later status can tell a pinned port
    /// apart from one that was observed. <see cref="ObservedPorts"/> carries
    /// what the gateway actually listens on.
    /// </remarks>
    [JsonPropertyName("port")]
    public int? Port { get; init; }

    /// <summary>The ports the running gateway was last observed listening on.</summary>
    [JsonPropertyName("observedPorts")]
    public IReadOnlyList<int>? ObservedPorts { get; init; }

    /// <summary>
    /// The per-launch status file written inside the session. Its path is
    /// unguessable, so a process left behind by an earlier launch cannot write
    /// to it and cannot present itself as the current generation.
    /// </summary>
    [JsonPropertyName("statusPath")]
    public string? StatusPath { get; init; }

    [JsonPropertyName("logPath")]
    public string? LogPath { get; init; }

    [JsonPropertyName("startedUtc")]
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>
    /// Set when the user explicitly turned logon recovery off, so a later
    /// manual start does not quietly turn it back on.
    /// </summary>
    [JsonPropertyName("autostartDisabled")]
    public bool AutostartDisabled { get; init; }
}

/// <summary>Why a stored gateway record could not be used.</summary>
internal enum GatewayStateFault
{
    /// <summary>No gateway has been recorded.</summary>
    Missing,

    /// <summary>The file exists but is not readable as a record.</summary>
    Unreadable,

    /// <summary>The record was written by an incompatible newer version.</summary>
    UnsupportedSchema,

    /// <summary>The record is missing values it cannot be used without.</summary>
    Incomplete,
}

internal sealed record GatewayStateResult(
    GatewayRecord? Record,
    GatewayStateFault? Fault,
    string? Detail)
{
    public static GatewayStateResult Loaded(GatewayRecord record) =>
        new(record, null, null);

    public static GatewayStateResult Failed(GatewayStateFault fault, string detail) =>
        new(null, fault, detail);
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GatewayRecord))]
internal sealed partial class GatewayStateJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes the gateway record.
/// </summary>
internal sealed class GatewayStateStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _filePath;

    public GatewayStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>
    /// Reads the record, distinguishing "no gateway" from "unusable record".
    /// </summary>
    /// <remarks>
    /// A damaged record never degrades to <see cref="GatewayStateFault.Missing"/>.
    /// Starting a replacement over a record we merely failed to read would
    /// abandon a running gateway that still holds its port.
    /// </remarks>
    public GatewayStateResult Read()
    {
        string text;
        try
        {
            text = File.ReadAllText(_filePath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return GatewayStateResult.Failed(
                GatewayStateFault.Missing,
                "No gateway has been recorded.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return GatewayStateResult.Failed(
                GatewayStateFault.Unreadable,
                $"The gateway record could not be read: {exception.Message}");
        }

        GatewayRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(
                text,
                GatewayStateJsonContext.Default.GatewayRecord);
        }
        catch (JsonException exception)
        {
            return GatewayStateResult.Failed(
                GatewayStateFault.Unreadable,
                $"The gateway record is not valid JSON: {exception.Message}");
        }

        if (record is null)
        {
            return GatewayStateResult.Failed(
                GatewayStateFault.Unreadable,
                "The gateway record is empty.");
        }

        if (record.SchemaVersion != CurrentSchemaVersion)
        {
            return GatewayStateResult.Failed(
                GatewayStateFault.UnsupportedSchema,
                $"The gateway record uses schema version {record.SchemaVersion}, " +
                $"which is newer than this installation supports " +
                $"({CurrentSchemaVersion}). A newer OpenClaw may be installed.");
        }

        if ((!record.LaunchPending && record.ProcessId <= 0) || string.IsNullOrWhiteSpace(record.SandboxId) ||
            record.ProcessStartTimeUtc == default || record.Port is < 1 or > 65535)
        {
            return GatewayStateResult.Failed(
                GatewayStateFault.Incomplete,
                "The gateway record does not identify a process in a session.");
        }

        return GatewayStateResult.Loaded(record);
    }

    public void Write(GatewayRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string text = JsonSerializer.Serialize(
            record with { SchemaVersion = CurrentSchemaVersion },
            GatewayStateJsonContext.Default.GatewayRecord);

        // Written through a temporary file so an interrupted write cannot leave
        // a truncated record that reads as unusable.
        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, text);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    /// <summary>Removes the record. Deleting nothing is success.</summary>
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

    /// <summary>
    /// Removes a record only when it names a session MXC explicitly reported
    /// stale and setup has replaced.
    /// </summary>
    /// <returns>True when the matching stale record was removed.</returns>
    public bool ClearForSupersededSession(string sandboxId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);

        GatewayStateResult state = Read();
        if (state.Fault == GatewayStateFault.Missing)
        {
            return false;
        }

        if (state.Record is null)
        {
            throw new SessionException(
                $"The gateway record could not be reconciled: {state.Detail}");
        }

        if (!string.Equals(state.Record.SandboxId, sandboxId, StringComparison.Ordinal))
        {
            return false;
        }

        Clear();
        return true;
    }
}
