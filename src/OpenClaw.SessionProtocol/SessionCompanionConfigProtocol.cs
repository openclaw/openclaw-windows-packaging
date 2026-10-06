using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenClaw.SessionProtocol;

public sealed record SessionCompanionConfigRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("checkOnly")]
    public bool CheckOnly { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("nodePath")]
    public string? NodePath { get; init; }

    [JsonPropertyName("applicationDirectory")]
    public string? ApplicationDirectory { get; init; }

    [JsonPropertyName("nativeRootPath")]
    public string? NativeRootPath { get; init; }

    [JsonPropertyName("preloadPath")]
    public string? PreloadPath { get; init; }

    [JsonPropertyName("environment")]
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

}

public sealed record SessionCompanionConfigResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionCompanionConfigRequest))]
[JsonSerializable(typeof(SessionCompanionConfigResult))]
internal sealed partial class SessionCompanionConfigJsonContext : JsonSerializerContext;

public static class SessionCompanionConfigProtocol
{
    public static string SerializeRequest(SessionCompanionConfigRequest request) =>
        JsonSerializer.Serialize(request, SessionCompanionConfigJsonContext.Default.SessionCompanionConfigRequest);

    public static string SerializeResult(SessionCompanionConfigResult result) =>
        JsonSerializer.Serialize(result, SessionCompanionConfigJsonContext.Default.SessionCompanionConfigResult);

    public static SessionCompanionConfigRequest ReadRequest(string json)
    {
        SessionCompanionConfigRequest request = Deserialize(
            json, SessionCompanionConfigJsonContext.Default.SessionCompanionConfigRequest, "request");
        RequireSchema(request.SchemaVersion, "request");
        bool hasValidPort = request.CheckOnly
            ? request.Port is >= 0 and <= 65535
            : request.Port is >= 1 and <= 65535;
        if (!IsSafeRequestId(request.RequestId) || !hasValidPort ||
            (request.Token is not null &&
             (request.CheckOnly || string.IsNullOrWhiteSpace(request.Token) ||
              request.Token.Length > 4096 ||
              request.Token.Any(char.IsControl))) ||
            request.NodePath is not { Length: > 0 } ||
            !Path.IsPathFullyQualified(request.NodePath) ||
            request.ApplicationDirectory is not { Length: > 0 } ||
            !Path.IsPathFullyQualified(request.ApplicationDirectory) ||
            (request.NativeRootPath is not null && !Path.IsPathFullyQualified(request.NativeRootPath)) ||
            (request.NativeRootPath is not null &&
             (request.PreloadPath is not { Length: > 0 } ||
              !Path.IsPathFullyQualified(request.PreloadPath))) ||
            request.Environment is null)
        {
            throw new SessionLaunchException("The Companion config request lacks a valid port or packaged runtime.");
        }
        return request;
    }

    public static SessionCompanionConfigResult ReadResult(string json, string expectedRequestId)
    {
        SessionCompanionConfigResult result = Deserialize(
            json, SessionCompanionConfigJsonContext.Default.SessionCompanionConfigResult, "result");
        RequireSchema(result.SchemaVersion, "result");
        if (!IsSafeRequestId(expectedRequestId) ||
            !string.Equals(result.RequestId, expectedRequestId, StringComparison.Ordinal) ||
            (result.Error is not null && string.IsNullOrWhiteSpace(result.Error)) ||
            (result.Error is null &&
             (result.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(result.Token))) ||
            (result.Error is not null && (result.Port != 0 || result.Token is not null)))
        {
            throw new SessionLaunchException("The Companion config result does not match the request.");
        }
        return result;
    }

    private static void RequireSchema(int version, string part)
    {
        if (version != SessionLaunchProtocol.CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"Companion config {part} schema version {version} is not supported; " +
                $"expected {SessionLaunchProtocol.CurrentSchemaVersion}.");
        }
    }

    private static bool IsSafeRequestId(string? value) =>
        value is not null &&
        Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z",
            RegexOptions.CultureInvariant);

    private static T Deserialize<T>(
        string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string part)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new SessionLaunchException($"The Companion config {part} is empty.");
        }
        catch (JsonException exception)
        {
            throw new SessionLaunchException(
                $"The Companion config {part} is invalid: {exception.Message}", exception);
        }
    }
}
