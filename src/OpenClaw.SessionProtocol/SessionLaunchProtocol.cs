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

    /// <summary>
    /// Whether the helper waits for the application or leaves it running.
    /// </summary>
    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter<SessionLaunchMode>))]
    public SessionLaunchMode Mode { get; init; } = SessionLaunchMode.Attached;

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

    /// <summary>
    /// A directory to put at the front of the launched process's <c>PATH</c>.
    /// </summary>
    /// <remarks>
    /// Named rather than resolved by the host: <see cref="Environment"/> is
    /// merged onto the guest account's own environment, and the host cannot
    /// know what that account's <c>PATH</c> already contains. This is how the
    /// packaged Node.js runtime wins over a machine-wide installation for tools
    /// that resolve <c>node</c> by name.
    /// </remarks>
    [JsonPropertyName("pathPrefix")]
    public string? PathPrefix { get; init; }

    /// <summary>
    /// Node.js options to append to the launched process's own
    /// <c>NODE_OPTIONS</c>.
    /// </summary>
    /// <remarks>
    /// Named rather than composed by the host, for the same reason
    /// <see cref="PathPrefix"/> is: <see cref="Environment"/> is assigned over
    /// the guest account's own environment, so a host-composed
    /// <c>NODE_OPTIONS</c> would replace whatever the agent already set. The
    /// host's own <c>NODE_OPTIONS</c> is not the agent's and must never reach
    /// the agent's Node.js processes.
    /// </remarks>
    [JsonPropertyName("nodeOptionsSuffix")]
    public string? NodeOptionsSuffix { get; init; }

    /// <summary>
    /// The application this launch runs and the staged native dependency root
    /// recorded for it. Present for every launch that runs packaged OpenClaw.
    /// </summary>
    /// <remarks>
    /// The helper admits the launch only when the recorded stage matches the
    /// installed application's native dependency inventory, and holds the root
    /// for the launched process's lifetime so setup can tell a root that still
    /// has a consumer from one that does not. An idle agent shell that merely
    /// inherited the root is a consumer too.
    /// </remarks>
    [JsonPropertyName("nativeStage")]
    public SessionNativeStageReference? NativeStage { get; init; }

    /// <summary>
    /// Detached only. Where the application's output is written, because a
    /// detached process has no console to inherit and the pipe it was started
    /// through closes as soon as the launching execution returns.
    /// </summary>
    [JsonPropertyName("logPath")]
    public string? LogPath { get; init; }

    /// <summary>
    /// Detached only. Where the supervising helper records that this launch
    /// generation is running or has exited.
    /// </summary>
    /// <remarks>
    /// The path is unguessable and unique per launch, so a process left behind
    /// by an earlier launch cannot write to it and cannot make itself look like
    /// the current generation.
    /// </remarks>
    [JsonPropertyName("statusPath")]
    public string? StatusPath { get; init; }

    /// <summary>
    /// Detached Gateway only. The bundled CLI command that consumes its
    /// restart handoff; the supervisor appends the exited child's expected PID.
    /// </summary>
    [JsonPropertyName("restartHandoffArguments")]
    public IReadOnlyList<string>? RestartHandoffArguments { get; init; }
}

/// <summary>
/// Names the native dependency stage a launch must be admitted against.
/// </summary>
/// <remarks>
/// The host names the installed application and the root setup recorded. Only
/// the agent can read that root, so the helper decides whether it is current.
/// A null root means setup staged nothing, which is admissible only while the
/// installed application carries no native dependencies.
/// </remarks>
public sealed record SessionNativeStageReference
{
    [JsonPropertyName("applicationDirectory")]
    public string? ApplicationDirectory { get; init; }

    [JsonPropertyName("rootPath")]
    public string? RootPath { get; init; }
}

/// <summary>
/// Whether a launch is awaited or left running.
/// </summary>
public enum SessionLaunchMode
{
    /// <summary>
    /// The helper waits for the application and returns its exit code. The
    /// console is inherited, so interactive and piped OpenClaw behave as they
    /// do on the host.
    /// </summary>
    Attached,

    /// <summary>
    /// The helper leaves the application running and reports the identity of
    /// the process that supervises it. Used for the gateway, which must outlive
    /// the execution that started it.
    /// </summary>
    Detached,

    /// <summary>
    /// The helper only admits the named native stage and releases it at once,
    /// starting nothing. A result with no error means a launch would be
    /// admitted. Used before an operation stops a running Gateway, so a refusal
    /// leaves it running.
    /// </summary>
    Admission,
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

    /// <summary>
    /// Detached only. The supervising process's identifier.
    /// </summary>
    [JsonPropertyName("processId")]
    public int? ProcessId { get; init; }

    /// <summary>
    /// Detached only. The supervising process's creation time.
    /// </summary>
    /// <remarks>
    /// Recorded with the identifier because Windows reuses process
    /// identifiers. The identifier alone would eventually name an unrelated
    /// process, which the gateway would then claim as its own and, worse,
    /// could be asked to stop.
    /// </remarks>
    [JsonPropertyName("processStartTimeUtc")]
    public DateTimeOffset? ProcessStartTimeUtc { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>
    /// Classifies <see cref="Error"/> when the host must act on the kind of
    /// failure, such as <see cref="SessionLaunchProtocol.NativeStageStaleErrorCode"/>.
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Raised when a launch request or result cannot be honored. The message is
/// written to the control result rather than to application output.
/// </summary>
public sealed class SessionLaunchException : Exception
{
    public SessionLaunchException()
    {
    }

    public SessionLaunchException(string message)
        : base(message)
    {
    }

    public SessionLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SessionLaunchException(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The result error code this failure reports, if any.</summary>
    public string? Code { get; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionLaunchRequest))]
[JsonSerializable(typeof(SessionLaunchResult))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;

public static class SessionLaunchProtocol
{
    /// <summary>
    /// Version of the public launcher-to-guest helper launch contract. This is
    /// independent of the MXC wire schema version; a mismatch means a stale
    /// file or a mixed installation and is always an error rather than
    /// something to interpret leniently.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Exit code used when the helper itself fails. It is deliberately
    /// ambiguous with an application exit code, which is exactly why the
    /// control result file is authoritative.
    /// </summary>
    public const int HelperFailureExitCode = 64;

    /// <summary>
    /// The recorded native dependency stage does not match the installed
    /// application, so nothing was started. Explicit setup repairs it.
    /// </summary>
    public const string NativeStageStaleErrorCode = "native-stage-stale";

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
                $"The launch request is not valid JSON: {exception.Message}",
                exception);
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

        if (request.Mode == SessionLaunchMode.Admission)
        {
            // Nothing is started, so the stage is the whole request.
            if (request.NativeStage is null)
            {
                throw new SessionLaunchException(
                    "An admission request names no native stage.");
            }
        }
        else if (string.IsNullOrWhiteSpace(request.Executable))
        {
            throw new SessionLaunchException("The launch request has no executable.");
        }
        else if (request.Arguments is null)
        {
            throw new SessionLaunchException("The launch request has no argument vector.");
        }
        else if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            // An absent working directory would silently become the system
            // directory, which is never what the caller asked for.
            throw new SessionLaunchException(
                "The launch request has no working directory.");
        }

        if (request.NativeStage is { } stage &&
            (!IsFullyQualifiedWithoutTraversal(stage.ApplicationDirectory) ||
             (stage.RootPath is not null && !IsFullyQualifiedWithoutTraversal(stage.RootPath))))
        {
            throw new SessionLaunchException(
                "The launch request names a native stage without fully qualified paths.");
        }

        if (request.Mode == SessionLaunchMode.Detached)
        {
            // A detached process has no console to inherit and the pipe it was
            // started through closes as soon as the launching execution
            // returns. Without a log it would write into a dead handle and
            // leave nothing behind to explain a failed start.
            if (string.IsNullOrWhiteSpace(request.LogPath))
            {
                throw new SessionLaunchException(
                    "A detached launch request has no log path.");
            }

            if (string.IsNullOrWhiteSpace(request.StatusPath))
            {
                throw new SessionLaunchException(
                    "A detached launch request has no status path.");
            }
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
                $"The launch result is not valid JSON: {exception.Message}",
                exception);
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

    /// <summary>
    /// Whether a path names a location outright, rather than relative to an
    /// unstated directory or by climbing out of the one it names.
    /// </summary>
    public static bool IsFullyQualifiedWithoutTraversal(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        !path.Split(['\\', '/']).Contains("..", StringComparer.Ordinal);
}
