using System.Text.Json.Serialization;

namespace OpenClaw.SessionHost;

internal sealed record StateActivationAsset(
    string RelativePath,
    TrustedPath.FileIdentity PreparedIdentity,
    TrustedPath.FileIdentity? OriginalIdentity = null,
    bool? HadOriginal = null);

internal sealed record StateActivationJournal
{
    public int SchemaVersion { get; init; } = 1;
    public string Kind { get; init; } = "profile-state-activation";
    public required string TransactionId { get; init; }
    public required string Profile { get; init; }
    public required TrustedPath.FileIdentity ProfileIdentity { get; init; }
    public required string SourceProfile { get; init; }
    public required string PreparedSha256 { get; init; }
    public string Phase { get; init; } = "prepared";
    public IReadOnlyList<StateActivationAsset> Assets { get; init; } = [];
    public string? ProtectionArchive { get; init; }
    public string? ProtectionSha256 { get; init; }
    public bool ProtectionCaptured { get; init; }
    public bool HadState { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StateActivationJournal))]
internal sealed partial class StateActivationJsonContext : JsonSerializerContext;
