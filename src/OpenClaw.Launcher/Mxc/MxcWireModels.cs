using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Mxc;

// Wire shapes for the pinned @microsoft/mxc-sdk state-aware protocol. They are
// deliberately internal: the preview envelope format must not leak into this
// package's public surface, because the published .NET SDK will replace this
// transport without changing IMxcSessionClient.

internal sealed class MxcRequestEnvelope
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = MxcWireProtocol.IsolationSessionSchemaVersion;

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    [JsonPropertyName("containment")]
    public string? Containment { get; set; }

    [JsonPropertyName("sandboxId")]
    public string? SandboxId { get; set; }

    [JsonPropertyName("correlationVector")]
    public string? CorrelationVector { get; set; }

    [JsonPropertyName("network")]
    public MxcNetworkAcknowledgement? Network { get; set; }

    [JsonPropertyName("process")]
    public MxcProcessConfig? Process { get; set; }

    [JsonPropertyName("experimental")]
    public MxcExperimentalSection? Experimental { get; set; }
}

/// <summary>
/// IsolationSession's required unrestricted-network acknowledgement. The only
/// accepted value is allow + local network; the constants are fixed rather than
/// configurable so this package cannot advertise a filter the backend refuses.
/// </summary>
internal sealed class MxcNetworkAcknowledgement
{
    [JsonPropertyName("defaultPolicy")]
    public string DefaultPolicy { get; set; } = "allow";

    [JsonPropertyName("allowLocalNetwork")]
    public bool AllowLocalNetwork { get; set; } = true;
}

internal sealed class MxcProcessConfig
{
    [JsonPropertyName("commandLine")]
    public string CommandLine { get; set; } = string.Empty;
}

internal sealed class MxcExperimentalSection
{
    [JsonPropertyName("isolation_session")]
    public MxcIsolationSessionPhases? IsolationSession { get; set; }
}

internal sealed class MxcIsolationSessionPhases
{
    [JsonPropertyName("provision")]
    public MxcIsolationSessionProvisionFields? Provision { get; set; }
}

internal sealed class MxcIsolationSessionProvisionFields
{
    [JsonPropertyName("appId")]
    public string? AppId { get; set; }
}

internal sealed class MxcResponseEnvelope
{
    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public MxcErrorEnvelope? Error { get; set; }
}

internal sealed class MxcErrorEnvelope
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Backend operation that failed. Observed on IsolationSession exec
    /// failures, e.g. "IsoSessionOps.RunProcessWithOptionsAsync".
    /// </summary>
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    /// <summary>Underlying Windows status, e.g. "0x80070520".</summary>
    [JsonPropertyName("nativeCode")]
    public string? NativeCode { get; set; }

    /// <summary>
    /// Backend-authored guidance. Surfaced verbatim because it names the
    /// actual recovery step more precisely than this package can infer.
    /// </summary>
    [JsonPropertyName("remediation")]
    public string? Remediation { get; set; }
}

internal sealed class MxcProvisionResultPayload
{
    [JsonPropertyName("sandboxId")]
    public string? SandboxId { get; set; }

    [JsonPropertyName("correlationVector")]
    public string? CorrelationVector { get; set; }

    [JsonPropertyName("metadata")]
    public MxcProvisionMetadataPayload? Metadata { get; set; }
}

internal sealed class MxcProvisionMetadataPayload
{
    [JsonPropertyName("agentUserName")]
    public string? AgentUserName { get; set; }

    [JsonPropertyName("agentUserSid")]
    public string? AgentUserSid { get; set; }

    [JsonPropertyName("ephemeralWorkspacePath")]
    public string? EphemeralWorkspacePath { get; set; }
}

internal sealed class MxcProbeResponse
{
    [JsonPropertyName("tier")]
    public string? Tier { get; set; }

    [JsonPropertyName("warnings")]
    public string[]? Warnings { get; set; }

    [JsonPropertyName("probes")]
    public MxcProbeDetails? Probes { get; set; }
}

internal sealed class MxcProbeDetails
{
    [JsonPropertyName("isolationSessionAvailable")]
    public bool? IsolationSessionAvailable { get; set; }

    [JsonPropertyName("baseContainerApiPresent")]
    public bool? BaseContainerApiPresent { get; set; }
}

// Source generation keeps serialization reflection-free so the NativeAOT
// executable behaves the same as the JIT test build.
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MxcRequestEnvelope))]
[JsonSerializable(typeof(MxcResponseEnvelope))]
[JsonSerializable(typeof(MxcProvisionResultPayload))]
[JsonSerializable(typeof(MxcProbeResponse))]
internal sealed partial class MxcJsonContext : JsonSerializerContext;
