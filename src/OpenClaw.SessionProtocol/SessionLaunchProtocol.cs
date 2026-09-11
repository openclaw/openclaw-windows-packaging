using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.SessionProtocol;

/// <summary>
/// A single OpenClaw invocation, expressed as data for the guest helper.
/// </summary>
/// <remarks>
/// The MXC execution API accepts one command-line string, which the pinned
/// backend flattens through <c>cmd.exe</c>. That silently expands
/// <c>%VAR%</c> and lets a quote in one argument truncate a later one, so the
/// real argument vector must never appear in that command line. It travels
/// here instead and is replayed verbatim by
/// <c>OpenClaw.SessionHost</c>.
/// </remarks>
public sealed record SessionLaunchRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    /// <summary>
    /// Identifies this invocation so the helper's control result cannot be
    /// confused with a concurrent invocation's.
    /// </summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("executable")]
    public string? Executable { get; init; }

    [JsonPropertyName("arguments")]
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>
    /// Required. Execution does not inherit the caller's working directory;
    /// without this the guest command would silently run in the system
    /// directory.
    /// </summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("environment")]
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// The guest helper's own outcome, kept separate from the launched
/// application's stdout, stderr, and exit code.
/// </summary>
/// <remarks>
/// Without this separation a helper that failed to start Node at all would be
/// indistinguishable from OpenClaw itself exiting with the same code.
/// </remarks>
public sealed record SessionLaunchResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>Whether the helper actually started the application.</summary>
    [JsonPropertyName("launched")]
    public bool Launched { get; init; }

    /// <summary>The application's exit code; null when it never started.</summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// Raised when a launch request or result cannot be honored. The message is
/// written to the control result rather than to application output.
/// </summary>
public sealed class SessionLaunchException(string message)
    : Exception(message);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionLaunchRequest))]
[JsonSerializable(typeof(SessionLaunchResult))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;

public static class SessionLaunchProtocol
{
    /// <summary>
    /// Incremented whenever the request or result shape changes. The helper and
    /// the launcher ship in the same package, so a mismatch means a stale file
    /// or a mixed installation and is always an error rather than something to
    /// interpret leniently.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Exit code used when the helper itself fails. It is deliberately
    /// ambiguous with an application exit code, which is exactly why the
    /// control result file is authoritative.
    /// </summary>
    public const int HelperFailureExitCode = 64;

    public static string ResultPathFor(string requestPath) =>
        requestPath + ".result.json";

    public static string SerializeRequest(SessionLaunchRequest request) =>
        JsonSerializer.Serialize(request, SessionJsonContext.Default.SessionLaunchRequest);

    public static string SerializeResult(SessionLaunchResult result) =>
        JsonSerializer.Serialize(result, SessionJsonContext.Default.SessionLaunchResult);

    /// <summary>
    /// Parses and fully validates a request. Validation happens here so the
    /// helper and its tests agree on what a usable request is.
    /// </summary>
    public static SessionLaunchRequest ReadRequest(string json)
    {
        SessionLaunchRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(
                json,
                SessionJsonContext.Default.SessionLaunchRequest);
        }
        catch (JsonException exception)
        {
            throw new SessionLaunchException(
                $"The launch request is not valid JSON: {exception.Message}");
        }

        if (request is null)
        {
            throw new SessionLaunchException("The launch request is empty.");
        }

        if (request.SchemaVersion != CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"Launch request schema version {request.SchemaVersion} is not " +
                $"supported; this helper implements version {CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new SessionLaunchException("The launch request has no request id.");
        }

        if (string.IsNullOrWhiteSpace(request.Executable))
        {
            throw new SessionLaunchException("The launch request has no executable.");
        }

        if (request.Arguments is null)
        {
            throw new SessionLaunchException("The launch request has no argument vector.");
        }

        // An absent working directory would silently become the system
        // directory, which is never what the caller asked for.
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            throw new SessionLaunchException(
                "The launch request has no working directory.");
        }

        return request;
    }

    public static SessionLaunchResult ReadResult(string json)
    {
        SessionLaunchResult? result;
        try
        {
            result = JsonSerializer.Deserialize(
                json,
                SessionJsonContext.Default.SessionLaunchResult);
        }
        catch (JsonException exception)
        {
            throw new SessionLaunchException(
                $"The launch result is not valid JSON: {exception.Message}");
        }

        if (result is null)
        {
            throw new SessionLaunchException("The launch result is empty.");
        }

        if (result.SchemaVersion != CurrentSchemaVersion)
        {
            throw new SessionLaunchException(
                $"Launch result schema version {result.SchemaVersion} is not " +
                $"supported; this launcher implements version {CurrentSchemaVersion}.");
        }

        if (result.Launched && string.IsNullOrWhiteSpace(result.RequestId))
        {
            // A result may legitimately carry no request id when the helper
            // failed before it could parse the request, but a launched
            // application must always be attributable to its invocation.
            throw new SessionLaunchException(
                "The launch result reports a launch but has no request id.");
        }

        return result;
    }
}
