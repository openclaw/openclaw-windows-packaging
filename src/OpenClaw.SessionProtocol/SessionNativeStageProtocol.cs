using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.SessionProtocol;

/// <summary>
/// Asks the guest to compare the recorded native stage with the installed
/// application without launching, staging, or loading anything.
/// </summary>
public sealed record SessionNativeStageInspectRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("applicationDirectory")]
    public string? ApplicationDirectory { get; init; }

    [JsonPropertyName("rootPath")]
    public string? RootPath { get; init; }
}

/// <summary>
/// Bounded source-versus-stage facts. Carries no paths, file contents, or
/// account names, only states, counts, digests, and package versions.
/// </summary>
public sealed record SessionNativeStageReport
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>
    /// The same state launch admission decides on, or <c>not-required</c>
    /// when the application carries no native dependencies and none is recorded.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary><c>package-inventory</c> or <c>unpackaged-source</c>.</summary>
    [JsonPropertyName("provenance")]
    public string? Provenance { get; init; }

    [JsonPropertyName("expectedContentId")]
    public string? ExpectedContentId { get; init; }

    [JsonPropertyName("stagedContentId")]
    public string? StagedContentId { get; init; }

    [JsonPropertyName("expectedFiles")]
    public int ExpectedFiles { get; init; }

    [JsonPropertyName("verifiedFiles")]
    public int VerifiedFiles { get; init; }

    [JsonPropertyName("missingFiles")]
    public int MissingFiles { get; init; }

    [JsonPropertyName("unexpectedFiles")]
    public int UnexpectedFiles { get; init; }

    [JsonPropertyName("changedFiles")]
    public int ChangedFiles { get; init; }

    [JsonPropertyName("packages")]
    public IReadOnlyList<SessionNativeStagePackage> Packages { get; init; } = [];

    /// <summary>Set when inspection itself failed; the other facts are then absent.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>One native-bearing package of the installed application.</summary>
public sealed record SessionNativeStagePackage
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; init; }

    [JsonPropertyName("stagedVersion")]
    public string? StagedVersion { get; init; }

    /// <summary>Digest of the package's expected files, as in the content identifier.</summary>
    [JsonPropertyName("sourceDigest")]
    public string? SourceDigest { get; init; }

    /// <summary>Digest of the same paths in the stage, or null when none are staged.</summary>
    [JsonPropertyName("stagedDigest")]
    public string? StagedDigest { get; init; }

    [JsonPropertyName("filesMatch")]
    public bool FilesMatch { get; init; }

    /// <summary>
    /// Where the redirect resolves this package's files, including any worker
    /// scripts it starts relative to itself: <c>staged</c>, <c>immutable</c>
    /// when the stage lacks the package, <c>mixed</c>, or <c>unknown</c> when
    /// a recorded stage could not be read: its layout was rejected, or it
    /// could not be leased, for example because its marker is gone.
    /// </summary>
    [JsonPropertyName("resolvesFrom")]
    public string? ResolvesFrom { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionNativeStageInspectRequest))]
[JsonSerializable(typeof(SessionNativeStageReport))]
internal sealed partial class SessionNativeStageJsonContext : JsonSerializerContext;

public static class SessionNativeStageProtocol
{
    /// <summary>The most packages a report carries.</summary>
    public const int MaximumPackages = 64;

    public static string SerializeRequest(SessionNativeStageInspectRequest request) =>
        JsonSerializer.Serialize(request, SessionNativeStageJsonContext.Default.SessionNativeStageInspectRequest);

    public static string SerializeReport(SessionNativeStageReport report) =>
        JsonSerializer.Serialize(report, SessionNativeStageJsonContext.Default.SessionNativeStageReport);

    public static SessionNativeStageInspectRequest ReadRequest(string json)
    {
        SessionNativeStageInspectRequest request = Deserialize(
            json, SessionNativeStageJsonContext.Default.SessionNativeStageInspectRequest, "request");
        RequireSchema(request.SchemaVersion, "request");
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            !SessionLaunchProtocol.IsFullyQualifiedWithoutTraversal(request.ApplicationDirectory) ||
            (request.RootPath is not null &&
             !SessionLaunchProtocol.IsFullyQualifiedWithoutTraversal(request.RootPath)))
        {
            throw new SessionLaunchException(
                "The native stage inspection request lacks a request id or fully qualified paths.");
        }

        return request;
    }

    public static SessionNativeStageReport ReadReport(string json, string expectedRequestId)
    {
        SessionNativeStageReport report = Deserialize(
            json, SessionNativeStageJsonContext.Default.SessionNativeStageReport, "report");
        RequireSchema(report.SchemaVersion, "report");
        if (!string.Equals(report.RequestId, expectedRequestId, StringComparison.Ordinal))
        {
            throw new SessionLaunchException("The native stage report does not match its request.");
        }

        if (report.Packages is null || report.Packages.Count > MaximumPackages ||
            (report.Error is null && string.IsNullOrWhiteSpace(report.State)))
        {
            throw new SessionLaunchException("The native stage report is malformed.");
        }

        return report;
    }

    private static void RequireSchema(int version, string part)
    {
        if (version != SessionLaunchProtocol.CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"Native stage {part} schema version {version} is not supported; " +
                $"expected {SessionLaunchProtocol.CurrentSchemaVersion}.");
        }
    }

    private static T Deserialize<T>(
        string json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string part)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new SessionLaunchException($"The native stage {part} is empty.");
        }
        catch (JsonException exception)
        {
            throw new SessionLaunchException(
                $"The native stage {part} could not be read: {exception.Message}", exception);
        }
    }
}
