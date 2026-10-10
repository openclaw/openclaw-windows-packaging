using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Mxc;

internal sealed class MxcRequestEnvelope
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = MxcWireProtocol.IsolationSessionSchemaVersion;

    [JsonPropertyName("containment")]
    public string? Containment { get; set; }

    [JsonPropertyName("network")]
    public MxcNetworkAcknowledgement? Network { get; set; }

    [JsonPropertyName("process")]
    public MxcProcessConfig? Process { get; set; }

    [JsonPropertyName("isolationSession")]
    public MxcIsolationSessionSection? IsolationSession { get; set; }
}

internal sealed record MxcCliRequest(
    string Operation,
    MxcSandboxId? ContainerId,
    MxcRequestEnvelope Config);

/// <summary>
/// IsolationSession provision's required unrestricted network acknowledgement.
/// MXC 1.0 accepts only allow for egress, ingress, and host loopback.
/// </summary>
internal sealed class MxcNetworkAcknowledgement
{
    [JsonPropertyName("egress")]
    public MxcNetworkDefaultPolicy Egress { get; } = new();

    [JsonPropertyName("ingress")]
    public MxcNetworkIngressPolicy Ingress { get; } = new();
}

internal sealed class MxcNetworkDefaultPolicy
{
    [JsonPropertyName("default")]
    public string Default { get; set; } = "allow";
}

internal sealed class MxcNetworkIngressPolicy
{
    [JsonPropertyName("default")]
    public string Default { get; set; } = "allow";

    [JsonPropertyName("hostLoopback")]
    public string HostLoopback { get; set; } = "allow";
}

internal sealed class MxcProcessConfig
{
    [JsonPropertyName("commandLine")]
    public string CommandLine { get; set; } = string.Empty;
}

internal sealed class MxcIsolationSessionSection
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

    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("nativeCode")]
    public string? NativeCode { get; set; }

    [JsonPropertyName("remediation")]
    public string? Remediation { get; set; }
}

internal sealed class MxcProvisionResultPayload
{
    [JsonPropertyName("sandboxId")]
    public string? SandboxId { get; set; }

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
