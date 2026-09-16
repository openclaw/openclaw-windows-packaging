using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.SessionProtocol;

/// <summary>
/// Asks the guest to install the packaged Node.js runtime into its own profile.
/// </summary>
/// <remarks>
/// <para>
/// The package ships Node.js as an archive and the host extracts it into the
/// invoking user's package LocalState, which the agent identity cannot read.
/// The archive itself is package content, readable by both identities, so the
/// guest extracts its own copy rather than being handed files it does not own.
/// </para>
/// <para>
/// The host names the archive; the guest decides where its own profile is,
/// because only it can resolve that. This is the same split the collect mode
/// uses, for the same reason.
/// </para>
/// </remarks>
public sealed record SessionRuntimeInstallRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>
    /// The packaged Node.js archive to install, as a fully qualified path.
    /// </summary>
    [JsonPropertyName("archivePath")]
    public string? ArchivePath { get; init; }

    /// <summary>
    /// Whether to prepend the installed directory to the agent's persistent
    /// user <c>PATH</c>.
    /// </summary>
    /// <remarks>
    /// Separate from the install itself so a caller that only wants the files
    /// does not silently change the account's environment.
    /// </remarks>
    [JsonPropertyName("updateUserPath")]
    public bool UpdateUserPath { get; init; } = true;
}

/// <summary>Where the guest put the runtime, and what it is.</summary>
public sealed record SessionRuntimeInstallResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>The agent-side <c>node.exe</c> the launcher must use.</summary>
    [JsonPropertyName("executablePath")]
    public string? ExecutablePath { get; init; }

    /// <summary>The version the installed runtime reported about itself.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>
    /// The archive the install came from, so a later launch can tell that the
    /// package has since shipped a different one.
    /// </summary>
    [JsonPropertyName("archiveName")]
    public string? ArchiveName { get; init; }

    /// <summary>Whether the agent's persistent user path was changed.</summary>
    [JsonPropertyName("userPathUpdated")]
    public bool UserPathUpdated { get; init; }

    /// <summary>Set when the install could not be completed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Asks the guest to install its OpenClaw command shim.</summary>
public sealed record SessionToolInstallRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("workspacePath")]
    public string? WorkspacePath { get; init; }
}

/// <summary>Reports guest-side command shim installation.</summary>
public sealed record SessionToolInstallResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("shimPath")]
    public string? ShimPath { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionRuntimeInstallRequest))]
[JsonSerializable(typeof(SessionRuntimeInstallResult))]
[JsonSerializable(typeof(SessionToolInstallRequest))]
[JsonSerializable(typeof(SessionToolInstallResult))]
internal sealed partial class SessionRuntimeJsonContext : JsonSerializerContext;

public static class SessionRuntimeProtocol
{
    public static string SerializeRequest(SessionRuntimeInstallRequest request) =>
        JsonSerializer.Serialize(
            request,
            SessionRuntimeJsonContext.Default.SessionRuntimeInstallRequest);

    public static string SerializeResult(SessionRuntimeInstallResult result) =>
        JsonSerializer.Serialize(
            result,
            SessionRuntimeJsonContext.Default.SessionRuntimeInstallResult);

    public static string SerializeToolInstallRequest(SessionToolInstallRequest request) =>
        JsonSerializer.Serialize(
            request,
            SessionRuntimeJsonContext.Default.SessionToolInstallRequest);

    public static string SerializeToolInstallResult(SessionToolInstallResult result) =>
        JsonSerializer.Serialize(
            result,
            SessionRuntimeJsonContext.Default.SessionToolInstallResult);

    public static SessionRuntimeInstallRequest ReadRequest(string json)
    {
        SessionRuntimeInstallRequest request = Deserialize(
            json,
            SessionRuntimeJsonContext.Default.SessionRuntimeInstallRequest,
            "runtime install request");

        RequireCurrentSchema(request.SchemaVersion, "install request", "helper");

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new SessionLaunchException(
                "The runtime install request has no request id.");
        }

        // A relative or traversing archive path would let a malformed request
        // name something outside the package this helper was staged from.
        if (string.IsNullOrWhiteSpace(request.ArchivePath) ||
            !Path.IsPathFullyQualified(request.ArchivePath) ||
            request.ArchivePath.Contains("..", StringComparison.Ordinal))
        {
            throw new SessionLaunchException(
                "The runtime install request has no fully qualified archive path.");
        }

        return request;
    }

    public static SessionToolInstallRequest ReadToolInstallRequest(string json)
    {
        SessionToolInstallRequest request = Deserialize(
            json,
            SessionRuntimeJsonContext.Default.SessionToolInstallRequest,
            "tool install request");
        RequireCurrentSchema(request.SchemaVersion, "tool install request", "helper");
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            throw new SessionLaunchException(
                "The tool install request is missing its request ID or workspace path.");
        }

        return request;
    }

    public static SessionToolInstallResult ReadToolInstallResult(string json)
    {
        SessionToolInstallResult result = Deserialize(
            json,
            SessionRuntimeJsonContext.Default.SessionToolInstallResult,
            "tool install result");
        RequireCurrentSchema(result.SchemaVersion, "tool install result", "launcher");
        return result;
    }

    public static SessionRuntimeInstallResult ReadResult(string json)
    {
        SessionRuntimeInstallResult result = Deserialize(
            json,
            SessionRuntimeJsonContext.Default.SessionRuntimeInstallResult,
            "runtime install result");

        RequireCurrentSchema(result.SchemaVersion, "install result", "launcher");
        return result;
    }

    private static void RequireCurrentSchema(int version, string subject, string reader)
    {
        if (version != SessionLaunchProtocol.CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"Runtime {subject} schema version {version} is not supported; " +
                $"this {reader} implements version " +
                $"{SessionLaunchProtocol.CurrentSchemaVersion}.");
        }
    }

    private static T Deserialize<T>(
        string json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string subject)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new SessionLaunchException($"The {subject} was empty.");
        }
        catch (JsonException exception)
        {
            throw new SessionLaunchException(
                $"The {subject} could not be read: {exception.Message}",
                exception);
        }
    }
}
