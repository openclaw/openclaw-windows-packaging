using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.SessionProtocol;

/// <summary>Asks the guest to classify its default OpenClaw config file.</summary>
public sealed record SessionConfigReadinessRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }
}

/// <summary>The file-only readiness state of the guest's default config.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionConfigReadinessState>))]
public enum SessionConfigReadinessState
{
    Absent,
    NotReady,
    StartupEligible
}

/// <summary>A stable explanation for a successful readiness classification.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionConfigReadinessReason>))]
public enum SessionConfigReadinessReason
{
    ConfigFileMissing,
    ConfigFileUnreadable,
    ConfigFileInvalid,
    GatewayMissing,
    GatewayModeMissing,
    GatewayModeNotLocal,
    GatewayModeLocal
}

/// <summary>The guest's file-only OpenClaw config classification.</summary>
public sealed record SessionConfigReadinessResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("state")]
    public SessionConfigReadinessState? State { get; init; }

    [JsonPropertyName("reason")]
    public SessionConfigReadinessReason? Reason { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionConfigReadinessRequest))]
[JsonSerializable(typeof(SessionConfigReadinessResult))]
internal sealed partial class SessionConfigReadinessJsonContext : JsonSerializerContext;

/// <summary>Serializes and validates config-readiness control messages.</summary>
public static class SessionConfigReadinessProtocol
{
    public static string SerializeRequest(SessionConfigReadinessRequest request) =>
        JsonSerializer.Serialize(
            request,
            SessionConfigReadinessJsonContext.Default.SessionConfigReadinessRequest);

    public static string SerializeResult(SessionConfigReadinessResult result) =>
        JsonSerializer.Serialize(
            result,
            SessionConfigReadinessJsonContext.Default.SessionConfigReadinessResult);

    public static SessionConfigReadinessRequest ReadRequest(string json)
    {
        SessionConfigReadinessRequest request = Deserialize(
            json,
            SessionConfigReadinessJsonContext.Default.SessionConfigReadinessRequest,
            "config readiness request");
        RequireCurrentSchema(request.SchemaVersion, "config readiness request", "helper");
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new SessionLaunchException(
                "The config readiness request has no request id.");
        }

        return request;
    }

    public static SessionConfigReadinessResult ReadResult(
        string json,
        string? expectedRequestId = null)
    {
        SessionConfigReadinessResult result = Deserialize(
            json,
            SessionConfigReadinessJsonContext.Default.SessionConfigReadinessResult,
            "config readiness result");
        RequireCurrentSchema(result.SchemaVersion, "config readiness result", "launcher");
        if (string.IsNullOrWhiteSpace(result.RequestId))
        {
            throw new SessionLaunchException(
                "The config readiness result has no request id.");
        }

        if (expectedRequestId is not null &&
            !string.Equals(result.RequestId, expectedRequestId, StringComparison.Ordinal))
        {
            throw new SessionLaunchException(
                "The config readiness result does not match the request.");
        }

        bool hasError = !string.IsNullOrWhiteSpace(result.Error);
        bool hasState = result.State is not null;
        bool hasReason = result.Reason is not null;
        if ((hasError && (hasState || hasReason)) ||
            (!hasError && (!hasState || !hasReason)))
        {
            throw new SessionLaunchException(
                "The config readiness result must contain either an error or a classification.");
        }

        if (!hasError && !IsCompatible(result.State!.Value, result.Reason!.Value))
        {
            throw new SessionLaunchException(
                "The config readiness result contains an incompatible state and reason.");
        }

        return result;
    }

    private static bool IsCompatible(
        SessionConfigReadinessState state,
        SessionConfigReadinessReason reason) =>
        state switch
        {
            SessionConfigReadinessState.Absent =>
                reason == SessionConfigReadinessReason.ConfigFileMissing,
            SessionConfigReadinessState.StartupEligible =>
                reason == SessionConfigReadinessReason.GatewayModeLocal,
            SessionConfigReadinessState.NotReady =>
                reason is not SessionConfigReadinessReason.ConfigFileMissing and
                    not SessionConfigReadinessReason.GatewayModeLocal,
            _ => false
        };

    private static T Deserialize<T>(
        string json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string description)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new SessionLaunchException(
                    $"The {description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new SessionLaunchException(
                $"The {description} is invalid: {exception.Message}",
                exception);
        }
    }

    private static void RequireCurrentSchema(
        int version,
        string description,
        string reader)
    {
        if (version != SessionLaunchProtocol.CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"The {reader} cannot read {description} schema version {version}; " +
                $"expected {SessionLaunchProtocol.CurrentSchemaVersion}.");
        }
    }
}
