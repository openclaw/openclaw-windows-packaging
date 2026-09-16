using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.SessionProtocol;

/// <summary>
/// Asks the guest whether a recorded gateway is still the process we started.
/// </summary>
/// <remarks>
/// The backend offers no authoritative process enumeration and nothing can be
/// reattached across an execution boundary, so liveness has to be re-established
/// from inside the session every time it is claimed.
/// </remarks>
public sealed record SessionInspectRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>The recorded supervising process.</summary>
    [JsonPropertyName("processId")]
    public int ProcessId { get; init; }

    /// <summary>
    /// The recorded creation time. A live process whose creation time differs
    /// is an unrelated process that inherited a reused identifier, never ours.
    /// </summary>
    [JsonPropertyName("processStartTimeUtc")]
    public DateTimeOffset ProcessStartTimeUtc { get; init; }

    [JsonPropertyName("helperPath")]
    public string? HelperPath { get; init; }

    /// <summary>
    /// Where the supervising helper records this launch generation. Its path is
    /// unguessable, so only the generation we started can have written it.
    /// </summary>
    [JsonPropertyName("statusPath")]
    public string? StatusPath { get; init; }

    /// <summary>
    /// The port the gateway was configured to listen on, when one was
    /// configured at all.
    /// </summary>
    /// <remarks>
    /// Usually absent. OpenClaw resolves its own gateway port from its own
    /// configuration, so this package supplies one only when the user set it
    /// explicitly; otherwise the guest reports which port it observed.
    /// </remarks>
    [JsonPropertyName("port")]
    public int? Port { get; init; }
}

/// <summary>
/// What the guest observed. Every field is an observation, never an inference.
/// </summary>
public sealed record SessionInspectResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>A process with the recorded identifier exists.</summary>
    [JsonPropertyName("processFound")]
    [JsonRequired]
    public bool ProcessFound { get; init; }

    /// <summary>
    /// That process's creation time matches the recorded one, so it is the
    /// process we started rather than one that inherited a reused identifier.
    /// </summary>
    [JsonPropertyName("startTimeMatches")]
    [JsonRequired]
    public bool StartTimeMatches { get; init; }

    /// <summary>What the supervising helper last recorded, if anything.</summary>
    [JsonPropertyName("supervisorState")]
    public string? SupervisorState { get; init; }

    /// <summary>What the supervising helper last recorded about its state.</summary>
    [JsonPropertyName("supervisorDetail")]
    public string? SupervisorDetail { get; init; }

    /// <summary>Something is listening on the recorded port.</summary>
    [JsonPropertyName("portListening")]
    [JsonRequired]
    public bool PortListening { get; init; }

    /// <summary>
    /// The ports the recorded supervisor or its descendants are listening on.
    /// </summary>
    /// <remarks>
    /// Reported rather than assumed: the gateway's port comes from OpenClaw's
    /// own configuration, so observing it is the only way to tell the user
    /// where their gateway actually is.
    /// </remarks>
    [JsonPropertyName("listeningPorts")]
    public IReadOnlyList<int>? ListeningPorts { get; init; }

    /// <summary>
    /// The listener belongs to the recorded supervisor or one of its
    /// descendants. Without this a gateway is only assumed from an open port,
    /// which any unrelated program could have opened.
    /// </summary>
    [JsonPropertyName("listenerOwned")]
    [JsonRequired]
    public bool ListenerOwned { get; init; }

    /// <summary>Set when the inspection itself could not be completed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>
    /// The gateway is running and is ours. Every part is required: a live
    /// process proves nothing without its creation time, and an open port
    /// proves nothing without its owner.
    /// </summary>
    [JsonIgnore]
    public bool IsOwnedAndHealthy =>
        Error is null &&
        ProcessFound &&
        StartTimeMatches &&
        PortListening &&
        ListenerOwned;
}

/// <summary>
/// What the supervising helper writes about its own launch generation.
/// </summary>
public sealed record SessionSupervisorStatus
{
    public const string RunningState = "running";
    public const string ExitedState = "exited";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionInspectRequest))]
[JsonSerializable(typeof(SessionInspectResult))]
[JsonSerializable(typeof(SessionSupervisorStatus))]
internal sealed partial class SessionInspectJsonContext : JsonSerializerContext;

public static class SessionInspectProtocol
{
    public static string SerializeRequest(SessionInspectRequest request) =>
        JsonSerializer.Serialize(
            request,
            SessionInspectJsonContext.Default.SessionInspectRequest);

    public static string SerializeResult(SessionInspectResult result) =>
        JsonSerializer.Serialize(
            result,
            SessionInspectJsonContext.Default.SessionInspectResult);

    public static string SerializeStatus(SessionSupervisorStatus status) =>
        JsonSerializer.Serialize(
            status,
            SessionInspectJsonContext.Default.SessionSupervisorStatus);

    public static SessionInspectRequest ReadRequest(string json)
    {
        SessionInspectRequest? request = Deserialize(
            json,
            SessionInspectJsonContext.Default.SessionInspectRequest,
            "inspect request");

        if (request.SchemaVersion != SessionLaunchProtocol.CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"Inspect request schema version {request.SchemaVersion} is not " +
                "supported; this helper implements version " +
                $"{SessionLaunchProtocol.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new SessionLaunchException("The inspect request has no request id.");
        }

        if (request.ProcessId <= 0)
        {
            throw new SessionLaunchException(
                "The inspect request has no process identifier.");
        }

        if (request.ProcessStartTimeUtc == default ||
            string.IsNullOrWhiteSpace(request.HelperPath) ||
            !Path.IsPathFullyQualified(request.HelperPath) ||
            request.Port is < 0 or > 65535)
        {
            throw new SessionLaunchException("The inspect request lacks a valid process identity, helper image, or port.");
        }

        return request;
    }

    public static SessionInspectResult ReadResult(string json)
    {
        SessionInspectResult result = Deserialize(
            json,
            SessionInspectJsonContext.Default.SessionInspectResult,
            "inspect result");

        if (result.SchemaVersion != SessionLaunchProtocol.CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"Inspect result schema version {result.SchemaVersion} is not " +
                "supported; this launcher implements version " +
                $"{SessionLaunchProtocol.CurrentSchemaVersion}.");
        }

        return result;
    }

    public static SessionSupervisorStatus ReadStatus(string json) =>
        Deserialize(
            json,
            SessionInspectJsonContext.Default.SessionSupervisorStatus,
            "supervisor status");

    private static T Deserialize<T>(
        string json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string description)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new SessionLaunchException($"The {description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new SessionLaunchException(
                $"The {description} is not valid JSON: {exception.Message}");
        }
    }
}
