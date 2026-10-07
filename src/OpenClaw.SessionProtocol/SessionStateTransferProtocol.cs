using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.SessionProtocol;

[JsonConverter(typeof(JsonStringEnumConverter<SessionStateTransferAction>))]
public enum SessionStateTransferAction
{
    Inspect,
    Capture,
    Preview,
    Prepare,
    Activate,
    Rollback,
    Recover
}

public sealed record SessionStateTransferRequest
{
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;
    public string Kind { get; init; } = SessionStateTransferProtocol.Kind;
    public string? RequestId { get; init; }
    public SessionStateTransferAction Action { get; init; }
    public string? TransactionId { get; init; }
    public string? WorkspaceDirectory { get; init; }
    public string? NodePath { get; init; }
    public string? ApplicationDirectory { get; init; }
    public string? NativeRootPath { get; init; }
    public string? NativePreloadPath { get; init; }
    public string? NodeOptionsSuffix { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public string? ArchivePath { get; init; }
    public string? ArchiveSha256 { get; init; }
    public string? SourceProfile { get; init; }
    public string? SourceDirectory { get; init; }
    public string? SourceSha256 { get; init; }
    public string? ProtectionArchive { get; init; }
    public string? ProtectionSha256 { get; init; }
    public bool DryRun { get; init; }
}

public sealed record StateTransferAsset(
    string Kind,
    string SourcePath,
    string ArchivePath);

public sealed record StateTransferMapping(
    string SourcePath,
    string ProfileRelativePath);

public sealed record StateTransferArchive(
    string Path,
    long Length,
    string Sha256,
    bool Verified);

public sealed record SessionStateTransferResult
{
    public int SchemaVersion { get; init; } = SessionLaunchProtocol.CurrentSchemaVersion;
    public string Kind { get; init; } = SessionStateTransferProtocol.Kind;
    public string? RequestId { get; init; }
    public string? ProfileDirectory { get; init; }
    public string? TransactionId { get; init; }
    public string? Phase { get; init; }
    public StateTransferArchive? Archive { get; init; }
    public IReadOnlyList<StateTransferAsset> Assets { get; init; } = [];
    public IReadOnlyList<StateTransferMapping> Mappings { get; init; } = [];
    public IReadOnlyList<string> RequiredSources { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? ProtectionArchive { get; init; }
    public bool HasState { get; init; }
    public bool Pending { get; init; }
    public bool GatewayStopped { get; init; }
    public SessionConfigReadinessResult? Readiness { get; init; }
    public string? Error { get; init; }
}

public static class SessionStateTransferProtocol
{
    public const string Kind = "state-transfer";

    public static string SerializeRequest(SessionStateTransferRequest request) =>
        JsonSerializer.Serialize(
            request,
            SessionStateTransferJsonContext.Default.SessionStateTransferRequest);

    public static string SerializeResult(SessionStateTransferResult result) =>
        JsonSerializer.Serialize(
            result,
            SessionStateTransferJsonContext.Default.SessionStateTransferResult);

    public static SessionStateTransferRequest ReadRequest(string json)
    {
        ValidateHeader(json);
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            if (!document.RootElement.TryGetProperty("action", out JsonElement action) ||
                action.ValueKind != JsonValueKind.String)
            {
                throw new SessionLaunchException("The state-transfer request has no named action.");
            }
        }
        SessionStateTransferRequest request = JsonSerializer.Deserialize(
            json,
            SessionStateTransferJsonContext.Default.SessionStateTransferRequest)
            ?? throw new SessionLaunchException("The state-transfer request is empty.");
        RequireRequestId(request.RequestId);
        if (!Enum.IsDefined(request.Action))
        {
            throw new SessionLaunchException("The state-transfer action is unsupported.");
        }

        RequireAbsolutePath(request.WorkspaceDirectory, "workspace");
        if (request.Action is not SessionStateTransferAction.Inspect and
            not SessionStateTransferAction.Rollback)
        {
            RequireAbsolutePath(request.NodePath, "agent Node.js executable");
            RequireAbsolutePath(request.ApplicationDirectory, "immutable application");
        }
        if (request.Action is SessionStateTransferAction.Preview or
            SessionStateTransferAction.Prepare)
        {
            RequireAbsolutePath(request.ArchivePath, "input archive");
            RequireHash(request.ArchiveSha256);
        }
        if (request.Action is SessionStateTransferAction.Prepare or
            SessionStateTransferAction.Activate)
        {
            RequireTransactionId(request.TransactionId);
        }
        if (request.Action == SessionStateTransferAction.Activate && request.ProtectionArchive is not null)
        {
            RequireAbsolutePath(request.ProtectionArchive, "retained protection archive");
            RequireHash(request.ProtectionSha256);
        }
        if (request.Action == SessionStateTransferAction.Recover)
        {
            RequireAbsolutePath(request.SourceProfile, "source profile");
            RequireAbsolutePath(request.SourceDirectory, "source staging directory");
            RequireHash(request.SourceSha256);
        }
        return request;
    }

    public static SessionStateTransferResult ReadResult(string json, string expectedRequestId)
    {
        ValidateHeader(json);
        SessionStateTransferResult result = JsonSerializer.Deserialize(
            json,
            SessionStateTransferJsonContext.Default.SessionStateTransferResult)
            ?? throw new SessionLaunchException("The state-transfer result is empty.");
        RequireRequestId(result.RequestId);
        if (!string.Equals(result.RequestId, expectedRequestId, StringComparison.Ordinal))
        {
            throw new SessionLaunchException("The state-transfer result does not match its request.");
        }
        if (result.Archive is { } archive)
        {
            RequireAbsolutePath(archive.Path, "archive");
            RequireHash(archive.Sha256);
            if (archive.Length <= 0 || !archive.Verified)
            {
                throw new SessionLaunchException("The guest did not produce a verified state archive.");
            }
            if (result.Readiness is { } readiness)
            {
                _ = SessionConfigReadinessProtocol.ReadResult(
                    SessionConfigReadinessProtocol.SerializeResult(readiness), expectedRequestId);
            }
        }
        if (result.Assets is null || result.Mappings is null ||
            result.RequiredSources is null || result.Warnings is null ||
            result.Assets.Any(asset => asset is null ||
                string.IsNullOrWhiteSpace(asset.Kind) ||
                string.IsNullOrWhiteSpace(asset.SourcePath) ||
                string.IsNullOrWhiteSpace(asset.ArchivePath)) ||
            result.Mappings.Any(mapping => mapping is null ||
                string.IsNullOrWhiteSpace(mapping.SourcePath) ||
                string.IsNullOrWhiteSpace(mapping.ProfileRelativePath)) ||
            result.RequiredSources.Any(string.IsNullOrWhiteSpace) ||
            result.Warnings.Any(string.IsNullOrWhiteSpace))
        {
            throw new SessionLaunchException("The state-transfer inventory is malformed.");
        }
        if (result.Assets.Count > 10000 || result.Mappings.Count > 10000 ||
            result.RequiredSources.Count > 10000 || result.Warnings.Count > 10000)
        {
            throw new SessionLaunchException("The state-transfer inventory is too large.");
        }
        return result;
    }

    private static void ValidateHeader(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("kind", out JsonElement kind) ||
            kind.ValueKind != JsonValueKind.String ||
            !string.Equals(kind.GetString(), Kind, StringComparison.Ordinal))
        {
            throw new SessionLaunchException(
                "The guest helper does not support state transfer. Run `clawctl setup` to refresh it.");
        }
        if (!root.TryGetProperty("schemaVersion", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) ||
            version != SessionLaunchProtocol.CurrentSchemaVersion)
        {
            throw new SessionLaunchException("The state-transfer schema version is unsupported.");
        }
    }

    private static void RequireRequestId(string? requestId)
    {
        if (!Guid.TryParseExact(requestId, "N", out _))
        {
            throw new SessionLaunchException("The state transfer has no valid request id.");
        }
    }

    private static void RequireTransactionId(string? transactionId)
    {
        if (!Guid.TryParseExact(transactionId, "N", out _))
        {
            throw new SessionLaunchException("The state transfer has no valid transaction id.");
        }
    }

    private static void RequireAbsolutePath(string? path, string subject)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new SessionLaunchException($"The state transfer has no absolute {subject} path.");
        }
    }

    private static void RequireHash(string? hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new SessionLaunchException("The state transfer has no valid SHA-256 digest.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionStateTransferRequest))]
[JsonSerializable(typeof(SessionStateTransferResult))]
internal sealed partial class SessionStateTransferJsonContext : JsonSerializerContext;
