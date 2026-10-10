using System.Text;
using System.Text.Json;

namespace OpenClaw.Launcher.Mxc;

internal static class MxcWireProtocol
{
    public const string IsolationSessionSchemaVersion = "1.0.0";

    public const string IsolationSessionContainment = "isolation_session";

    public const string ProvisionPhase = "provision";
    public const string StartPhase = "start";
    public const string ExecPhase = "exec";
    public const string StopPhase = "stop";
    public const string DeprovisionPhase = "deprovision";

    public static MxcCliRequest BuildProvisionRequest(string appId) =>
        new(
            ProvisionPhase,
            ContainerId: null,
            new MxcRequestEnvelope
            {
                Containment = IsolationSessionContainment,
                Network = new MxcNetworkAcknowledgement(),
                IsolationSession = new MxcIsolationSessionSection
                {
                    Provision = new MxcIsolationSessionProvisionFields
                    {
                        AppId = appId
                    }
                }
            });

    public static MxcCliRequest BuildPhaseRequest(
        string phase,
        MxcSandboxId sandboxId,
        string? commandLine = null)
    {
        if (phase is not (StartPhase or ExecPhase or StopPhase or DeprovisionPhase))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                $"Unsupported post-provision operation '{phase}'.");
        }

        if (!sandboxId.IsIsolationSession)
        {
            throw new MxcException(
                MxcErrorCode.MalformedId,
                $"Sandbox id prefix '{sandboxId.BackendPrefix}' is not an " +
                "IsolationSession identity.");
        }

        if (phase == ExecPhase && string.IsNullOrWhiteSpace(commandLine))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                "An exec operation requires a command line.");
        }

        if (phase != ExecPhase && commandLine is not null)
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                $"The {phase} operation cannot carry a command line.");
        }

        return new MxcCliRequest(
            phase,
            sandboxId,
            new MxcRequestEnvelope
            {
                Process = commandLine is null
                    ? null
                    : new MxcProcessConfig { CommandLine = commandLine }
            });
    }

    public static IReadOnlyList<string> BuildArguments(MxcCliRequest request)
    {
        if (request.Operation is not (
            ProvisionPhase or StartPhase or ExecPhase or StopPhase or DeprovisionPhase))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                $"Unsupported MXC operation '{request.Operation}'.");
        }

        bool isProvision = request.Operation == ProvisionPhase;
        if (isProvision == request.ContainerId.HasValue)
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                isProvision
                    ? "Provision cannot specify a container id."
                    : "A lifecycle operation requires a container id.");
        }

        List<string> arguments =
        [
            "--config-base64",
            EncodeConfig(request.Config),
            "--operation",
            request.Operation
        ];

        if (request.ContainerId is MxcSandboxId containerId)
        {
            if (!containerId.IsIsolationSession)
            {
                throw new MxcException(
                    MxcErrorCode.MalformedId,
                    $"Sandbox id prefix '{containerId.BackendPrefix}' is not an " +
                    "IsolationSession identity.");
            }

            arguments.Add("--container-id");
            arguments.Add(containerId.Value);
        }

        return arguments;
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
            provisionMetadata);
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
            error.Code,
            operation: error.Operation);
    }
}
