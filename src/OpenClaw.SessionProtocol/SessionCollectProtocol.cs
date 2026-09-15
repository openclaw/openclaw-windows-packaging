using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.SessionProtocol;

/// <summary>
/// Asks the guest to stage diagnostic files into the shared workspace.
/// </summary>
/// <remarks>
/// <para>
/// The agent profile is ACL'd against the invoking user, so agent-side logs are
/// unreadable from the host even though the host owns the machine. Staging
/// through the shared workspace is what makes them readable without elevation
/// or any ACL change.
/// </para>
/// <para>
/// The host names every source explicitly. The guest never decides what is
/// interesting, so a future host change cannot silently widen what leaves the
/// session.
/// </para>
/// </remarks>
public sealed record SessionCollectRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>
    /// Where the guest writes the staged copies. The host owns this directory
    /// and removes it once the bundle is written.
    /// </summary>
    [JsonPropertyName("destinationDirectory")]
    public string? DestinationDirectory { get; init; }

    [JsonPropertyName("sources")]
    public IReadOnlyList<SessionCollectSource>? Sources { get; init; }

    /// <summary>
    /// File names the guest must never copy, matched case-insensitively against
    /// the file name with <c>*</c> as a trailing wildcard.
    /// </summary>
    /// <remarks>
    /// Credential stores live beside the logs worth collecting, so the deny
    /// list travels with the request and is enforced again by the host when the
    /// bundle is written. Either check alone would be a single point of failure.
    /// </remarks>
    [JsonPropertyName("deniedNames")]
    public IReadOnlyList<string>? DeniedNames { get; init; }
}

/// <summary>One file or directory to stage, and the name to stage it under.</summary>
public sealed record SessionCollectSource
{
    /// <summary>
    /// Where to read from, relative to the <em>agent's</em> user profile.
    /// </summary>
    /// <remarks>
    /// Deliberately not an absolute path. The host cannot know the agent's
    /// profile directory — the account name is generated and the directory is
    /// ACL'd against the host user — so an absolute path built on the host
    /// names the host's own profile and silently collects nothing. Only the
    /// guest can resolve this, and it resolves it against its own profile.
    /// </remarks>
    [JsonPropertyName("relativePath")]
    public string? RelativePath { get; init; }

    /// <summary>
    /// The relative name inside the staging directory, which keeps guest paths
    /// (and the agent account name in them) out of the bundle layout.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Which files in a directory source to take. Defaults to all of them.
    /// </summary>
    /// <remarks>
    /// The directories worth collecting also hold content that is large,
    /// uninteresting, or private: shell completions run to megabytes and the
    /// agent's workspace is its own content. Narrowing here rather than on the
    /// host keeps that content from ever leaving the session.
    /// </remarks>
    [JsonPropertyName("pattern")]
    public string? Pattern { get; init; }

    /// <summary>Whether a directory source is copied recursively.</summary>
    [JsonPropertyName("recursive")]
    public bool Recursive { get; init; }
}

/// <summary>What the guest actually staged.</summary>
/// <remarks>
/// A missing source is ordinary: collection runs precisely when an installation
/// is broken. Each outcome is reported per entry so the bundle manifest can say
/// what was attempted rather than only what succeeded.
/// </remarks>
public sealed record SessionCollectResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("entries")]
    public IReadOnlyList<SessionCollectEntry>? Entries { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>The outcome for one staged file.</summary>
public sealed record SessionCollectEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("copied")]
    public bool Copied { get; init; }

    [JsonPropertyName("length")]
    public long Length { get; init; }

    /// <summary>Why this entry was not copied, when it was not.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionCollectRequest))]
[JsonSerializable(typeof(SessionCollectResult))]
internal sealed partial class SessionCollectJsonContext : JsonSerializerContext;

public static class SessionCollectProtocol
{
    public static string SerializeRequest(SessionCollectRequest request) =>
        JsonSerializer.Serialize(
            request,
            SessionCollectJsonContext.Default.SessionCollectRequest);

    public static string SerializeResult(SessionCollectResult result) =>
        JsonSerializer.Serialize(
            result,
            SessionCollectJsonContext.Default.SessionCollectResult);

    public static SessionCollectRequest ReadRequest(string json)
    {
        SessionCollectRequest request = Deserialize(
            json,
            SessionCollectJsonContext.Default.SessionCollectRequest,
            "collect request");

        if (request.SchemaVersion != SessionLaunchProtocol.CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"Collect request schema version {request.SchemaVersion} is not " +
                "supported; this helper implements version " +
                $"{SessionLaunchProtocol.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new SessionLaunchException("The collect request has no request id.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory) ||
            !Path.IsPathFullyQualified(request.DestinationDirectory))
        {
            throw new SessionLaunchException(
                "The collect request has no fully qualified destination directory.");
        }

        foreach (SessionCollectSource source in request.Sources ?? [])
        {
            // Rooted or traversing paths would read outside the agent profile,
            // which is the one place a collect source is allowed to name.
            if (string.IsNullOrWhiteSpace(source.RelativePath) ||
                Path.IsPathRooted(source.RelativePath) ||
                source.RelativePath.Contains("..", StringComparison.Ordinal))
            {
                throw new SessionLaunchException(
                    "A collect source is not a safe profile-relative path: " +
                    $"{source.RelativePath}");
            }

            // A name that escapes the staging directory would let a malformed
            // request write anywhere the agent can reach.
            if (string.IsNullOrWhiteSpace(source.Name) ||
                Path.IsPathRooted(source.Name) ||
                source.Name.Contains("..", StringComparison.Ordinal))
            {
                throw new SessionLaunchException(
                    $"The collect source name is not a safe relative name: {source.Name}");
            }
        }

        return request;
    }

    public static SessionCollectResult ReadResult(string json)
    {
        SessionCollectResult result = Deserialize(
            json,
            SessionCollectJsonContext.Default.SessionCollectResult,
            "collect result");

        if (result.SchemaVersion != SessionLaunchProtocol.CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"Collect result schema version {result.SchemaVersion} is not " +
                "supported; this launcher implements version " +
                $"{SessionLaunchProtocol.CurrentSchemaVersion}.");
        }

        return result;
    }

    /// <summary>
    /// Whether a file name is denied, with <c>*</c> supported only as a
    /// trailing wildcard.
    /// </summary>
    /// <remarks>
    /// Deliberately not a glob engine. The deny list guards credential stores,
    /// so it has to behave identically in the guest and the host, and a shared
    /// literal-or-prefix rule is small enough to be obviously the same.
    /// </remarks>
    public static bool IsDenied(string fileName, IReadOnlyList<string>? deniedNames)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        foreach (string denied in deniedNames ?? [])
        {
            if (denied.EndsWith('*'))
            {
                if (fileName.StartsWith(
                        denied[..^1], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(fileName, denied, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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
