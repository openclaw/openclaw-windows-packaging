using System.Text;
using System.Text.Json;

namespace OpenClaw.Launcher.Mxc;

/// <summary>
/// Builds and interprets the pinned MXC state-aware wire protocol: a base64
/// UTF-8 JSON request envelope on the command line, and a single JSON response
/// envelope on standard output for every phase except execution.
/// </summary>
internal static class MxcWireProtocol
{
    /// <summary>
    /// Schema version stamped on IsolationSession envelopes. This is the wire
    /// schema, which is versioned separately from the npm package version
    /// recorded in mxc-runtime.lock.json; both are pinned independently.
    /// </summary>
    public const string IsolationSessionSchemaVersion = "0.6.0-alpha";

    public const string IsolationSessionContainment = "isolation_session";

    public const string ProvisionPhase = "provision";
    public const string StartPhase = "start";
    public const string ExecPhase = "exec";
    public const string StopPhase = "stop";
    public const string DeprovisionPhase = "deprovision";

    public static MxcRequestEnvelope BuildProvisionEnvelope(string appId) =>
        new()
        {
            Phase = ProvisionPhase,
            Containment = IsolationSessionContainment,
            Network = new MxcNetworkAcknowledgement(),
            Experimental = new MxcExperimentalSection
            {
                IsolationSession = new MxcIsolationSessionPhases
                {
                    Provision = new MxcIsolationSessionProvisionFields
                    {
                        AppId = appId
                    }
                }
            }
        };

    /// <summary>
    /// Builds a post-provision envelope. Network policy and application
    /// identity are fixed at provision and are rejected on later phases, so
    /// they are deliberately absent here.
    /// </summary>
    public static MxcRequestEnvelope BuildPhaseEnvelope(
        string phase,
        MxcSandboxId sandboxId,
        string? correlationVector,
        string? commandLine = null)
    {
        if (!sandboxId.IsIsolationSession)
        {
            throw new MxcException(
                MxcErrorCode.MalformedId,
                $"Sandbox id prefix '{sandboxId.BackendPrefix}' is not an " +
                "IsolationSession identity.");
        }

        return new MxcRequestEnvelope
        {
            Phase = phase,
            SandboxId = sandboxId.Value,
            CorrelationVector = correlationVector,
            Process = commandLine is null
                ? null
                : new MxcProcessConfig { CommandLine = commandLine }
        };
    }

    public static string EncodeConfig(MxcRequestEnvelope envelope)
    {
        string json = JsonSerializer.Serialize(
            envelope,
            MxcJsonContext.Default.MxcRequestEnvelope);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    internal static MxcRequestEnvelope DecodeConfig(string configBase64)
    {
        string json = Encoding.UTF8.GetString(Convert.FromBase64String(configBase64));
        return JsonSerializer.Deserialize(
                json,
                MxcJsonContext.Default.MxcRequestEnvelope)
            ?? throw new MxcException(
                MxcErrorCode.ProtocolViolation,
                "Request envelope decoded to null.");
    }

    /// <summary>
    /// Parses the single response envelope produced by a non-execution phase.
    /// An <c>{error}</c> envelope is raised as an <see cref="MxcException"/>
    /// carrying the backend's own code.
    /// </summary>
    public static JsonElement ParseNonExecutionResponse(string standardOutput)
    {
        MxcResponseEnvelope envelope = DeserializeResponse(standardOutput);
        if (envelope.Error is not null)
        {
            throw ToException(envelope.Error);
        }

        if (envelope.Result is null)
        {
            throw new MxcException(
                MxcErrorCode.ProtocolViolation,
                "MXC response contained neither a result nor an error.");
        }

        return envelope.Result.Value;
    }

    /// <summary>
    /// Discriminates an MXC dispatch failure from ordinary command output.
    /// </summary>
    /// <remarks>
    /// Execution forwards the guest command's raw output, so output that merely
    /// happens to be JSON must not be reported as a dispatch error. Only a
    /// complete <c>{error:{code}}</c> envelope qualifies.
    /// </remarks>
    public static MxcException? TryParseExecutionError(string standardOutput)
    {
        MxcResponseEnvelope envelope;
        try
        {
            envelope = DeserializeResponse(standardOutput);
        }
        catch (MxcException)
        {
            return null;
        }

        return string.IsNullOrEmpty(envelope.Error?.Code)
            ? null
            : ToException(envelope.Error!);
    }

    public static MxcProvisionResult ReadProvisionResult(JsonElement result)
    {
        MxcProvisionResultPayload? payload;
        try
        {
            payload = result.Deserialize(
                MxcJsonContext.Default.MxcProvisionResultPayload);
        }
        catch (JsonException exception)
        {
            throw new MxcException(
                MxcErrorCode.ProtocolViolation,
                "MXC provision result could not be interpreted.",
                innerException: exception);
        }

        if (string.IsNullOrWhiteSpace(payload?.SandboxId))
        {
            throw new MxcException(
                MxcErrorCode.ProtocolViolation,
                "MXC provision result did not include a sandbox id.");
        }

        // Metadata is reported only when the backend supplies every field. A
        // partially populated workspace/agent identity is worse than none: it
        // would let later phases act on an unusable path or account.
        MxcProvisionMetadataPayload? metadata = payload.Metadata;
        MxcProvisionMetadata? provisionMetadata =
            metadata is null ||
            string.IsNullOrWhiteSpace(metadata.AgentUserName) ||
            string.IsNullOrWhiteSpace(metadata.AgentUserSid) ||
            string.IsNullOrWhiteSpace(metadata.EphemeralWorkspacePath)
                ? null
                : new MxcProvisionMetadata(
                    metadata.AgentUserName,
                    metadata.AgentUserSid,
                    metadata.EphemeralWorkspacePath);

        return new MxcProvisionResult(
            MxcSandboxId.Parse(payload.SandboxId),
            provisionMetadata,
            payload.CorrelationVector);
    }

    private static MxcResponseEnvelope DeserializeResponse(string standardOutput)
    {
        try
        {
            return JsonSerializer.Deserialize(
                    standardOutput.Trim(),
                    MxcJsonContext.Default.MxcResponseEnvelope)
                ?? throw new MxcException(
                    MxcErrorCode.ProtocolViolation,
                    "MXC response envelope was empty.");
        }
        catch (JsonException exception)
        {
            throw new MxcException(
                MxcErrorCode.ProtocolViolation,
                "MXC response was not a JSON envelope.",
                innerException: exception);
        }
    }

    /// <summary>
    /// Reads the executor's <c>--probe</c> output. This is plain JSON, not a
    /// lifecycle result/error envelope, so it is parsed separately.
    /// </summary>
    public static MxcBackendProbe ReadProbeResponse(string standardOutput)
    {
        MxcProbeResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                standardOutput,
                MxcJsonContext.Default.MxcProbeResponse);
        }
        catch (JsonException exception)
        {
            throw new MxcException(
                MxcErrorCode.ProtocolViolation,
                "The MXC host capability probe returned malformed JSON.",
                innerException: exception);
        }

        if (payload?.Probes?.IsolationSessionAvailable is not bool available)
        {
            throw new MxcException(
                MxcErrorCode.ProtocolViolation,
                "The MXC host capability probe did not report " +
                "isolationSessionAvailable.");
        }

        return new MxcBackendProbe(
            available,
            payload.Tier,
            payload.Warnings ?? []);
    }

    private static MxcException ToException(MxcErrorEnvelope error)
    {
        string message = error.Message is { Length: > 0 }
            ? error.Message
            : $"MXC reported error '{error.Code ?? "unspecified"}'.";

        // The backend's own remediation text names the recovery step more
        // precisely than this package can infer from the code alone, so it is
        // appended verbatim rather than replaced with a generic hint.
        if (error.Remediation is { Length: > 0 })
        {
            message = $"{message} {error.Remediation}";
        }

        if (error.NativeCode is { Length: > 0 })
        {
            message = $"{message} (native code {error.NativeCode})";
        }

        return new MxcException(
            MxcException.Classify(error.Code),
            message,
            error.Code);
    }
}
