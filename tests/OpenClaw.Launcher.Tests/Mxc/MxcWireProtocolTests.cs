using System.Text;
using System.Text.Json;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcWireProtocolTests
{
    private const string SandboxIdValue = "iso:abc123";

    [Fact]
    public void ProvisionEnvelopeCarriesApplicationIdentityAndNetworkAcknowledgement()
    {
        MxcRequestEnvelope envelope =
            MxcWireProtocol.BuildProvisionEnvelope("PFN:Contoso.App_8wekyb3d8bbwe");

        JsonElement encoded = Decode(MxcWireProtocol.EncodeConfig(envelope));

        Assert.Equal(
            MxcWireProtocol.IsolationSessionSchemaVersion,
            encoded.GetProperty("version").GetString());
        Assert.Equal("provision", encoded.GetProperty("phase").GetString());
        Assert.Equal(
            "isolation_session",
            encoded.GetProperty("containment").GetString());
        Assert.Equal(
            "allow",
            encoded.GetProperty("network").GetProperty("defaultPolicy").GetString());
        Assert.True(
            encoded.GetProperty("network")
                .GetProperty("allowLocalNetwork")
                .GetBoolean());

        // The backend only accepts appId nested per backend and phase; a
        // top-level appId is silently ignored and the session would then be
        // provisioned without this package's identity.
        Assert.Equal(
            "PFN:Contoso.App_8wekyb3d8bbwe",
            encoded.GetProperty("experimental")
                .GetProperty("isolation_session")
                .GetProperty("provision")
                .GetProperty("appId")
                .GetString());
        Assert.False(encoded.TryGetProperty("appId", out _));
        Assert.False(encoded.TryGetProperty("sandboxId", out _));
    }

    [Fact]
    public void PostProvisionEnvelopeOmitsPolicyFixedAtProvision()
    {
        MxcRequestEnvelope envelope = MxcWireProtocol.BuildPhaseEnvelope(
            MxcWireProtocol.ExecPhase,
            MxcSandboxId.Parse(SandboxIdValue),
            "cv-1",
            "\"C:\\Program Files\\helper.exe\" --request r.json");

        JsonElement encoded = Decode(MxcWireProtocol.EncodeConfig(envelope));

        Assert.Equal("exec", encoded.GetProperty("phase").GetString());
        Assert.Equal(SandboxIdValue, encoded.GetProperty("sandboxId").GetString());
        Assert.Equal("cv-1", encoded.GetProperty("correlationVector").GetString());
        Assert.Equal(
            "\"C:\\Program Files\\helper.exe\" --request r.json",
            encoded.GetProperty("process").GetProperty("commandLine").GetString());

        // network and appId are fixed at provision and are rejected on later
        // phases, so resending them would fail the whole request. Confirmed
        // against wxc-exec.exe 0.8.0, which rejects a post-provision
        // containment with malformed_request: "State-aware 'stop' requests
        // must not carry 'containment'".
        Assert.False(encoded.TryGetProperty("network", out _));
        Assert.False(encoded.TryGetProperty("containment", out _));
        Assert.False(encoded.TryGetProperty("experimental", out _));
    }

    [Fact]
    public void PhaseEnvelopeOmitsProcessWhenNoCommandIsSupplied()
    {
        JsonElement encoded = Decode(MxcWireProtocol.EncodeConfig(
            MxcWireProtocol.BuildPhaseEnvelope(
                MxcWireProtocol.StopPhase,
                MxcSandboxId.Parse(SandboxIdValue),
                correlationVector: null)));

        Assert.False(encoded.TryGetProperty("process", out _));
        Assert.False(encoded.TryGetProperty("correlationVector", out _));
    }

    [Fact]
    public void PhaseEnvelopeRejectsAnotherBackendsSandboxId()
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.BuildPhaseEnvelope(
                MxcWireProtocol.StartPhase,
                MxcSandboxId.Parse("wsb:abc123"),
                correlationVector: null));

        Assert.Equal(MxcErrorCode.MalformedId, exception.Code);
    }

    [Theory]
    [InlineData("policy_validation", nameof(MxcErrorCode.PolicyValidation))]
    [InlineData("stale_id", nameof(MxcErrorCode.StaleId))]
    [InlineData("malformed_id", nameof(MxcErrorCode.MalformedId))]
    [InlineData("some_future_code", nameof(MxcErrorCode.Unknown))]
    public void ErrorEnvelopeIsRaisedWithTheBackendsOwnCode(
        string backendCode,
        string expectedName)
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.ParseNonExecutionResponse(
                $"{{\"error\":{{\"code\":\"{backendCode}\",\"message\":\"denied\"}}}}"));

        Assert.Equal(Enum.Parse<MxcErrorCode>(expectedName), exception.Code);
        Assert.Equal(backendCode, exception.BackendCode);
        Assert.Equal("denied", exception.Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"unexpected\":1}")]
    public void UninterpretableResponsesAreProtocolViolations(string output)
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.ParseNonExecutionResponse(output));

        Assert.Equal(MxcErrorCode.ProtocolViolation, exception.Code);
    }

    [Theory]
    [InlineData("hello from the sandbox")]
    [InlineData("{\"error\":\"something the command printed\"}")]
    [InlineData("{\"error\":{\"message\":\"no code\"}}")]
    [InlineData("{\"result\":{\"sandboxId\":\"iso:x\"}}")]
    public void CommandOutputIsNotMistakenForADispatchFailure(string output) =>
        Assert.Null(MxcWireProtocol.TryParseExecutionError(output));

    [Fact]
    public void DispatchFailureDuringExecutionIsRecognized()
    {
        MxcException? exception = MxcWireProtocol.TryParseExecutionError(
            """{"error":{"code":"stale_id","message":"gone"}}""");

        Assert.NotNull(exception);
        Assert.Equal(MxcErrorCode.StaleId, exception.Code);
    }

    [Fact]
    public void ProvisionResultReportsIdentityAndWorkspaceMetadata()
    {
        MxcProvisionResult result = MxcWireProtocol.ReadProvisionResult(
            MxcWireProtocol.ParseNonExecutionResponse(
                """
                {"result":{"sandboxId":"iso:abc123","correlationVector":"cv-1",
                "metadata":{"agentUserName":"MxcAgent_1","agentUserSid":"S-1-5-21-1",
                "ephemeralWorkspacePath":"C:\\ws\\1"}}}
                """));

        Assert.Equal("iso:abc123", result.SandboxId.Value);
        Assert.True(result.SandboxId.IsIsolationSession);
        Assert.Equal("cv-1", result.CorrelationVector);
        Assert.Equal("MxcAgent_1", result.Metadata?.AgentUserName);
        Assert.Equal("S-1-5-21-1", result.Metadata?.AgentUserSid);
        Assert.Equal("C:\\ws\\1", result.Metadata?.EphemeralWorkspacePath);
    }

    [Fact]
    public void ProvisionResultWithoutASandboxIdIsAProtocolViolation()
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.ReadProvisionResult(
                MxcWireProtocol.ParseNonExecutionResponse(
                    """{"result":{"correlationVector":"cv-1"}}""")));

        Assert.Equal(MxcErrorCode.ProtocolViolation, exception.Code);
    }

    [Fact]
    public void PartialProvisionMetadataIsReportedAsAbsent()
    {
        // Half a workspace identity is worse than none: later phases would act
        // on an unusable path or account.
        MxcProvisionResult result = MxcWireProtocol.ReadProvisionResult(
            MxcWireProtocol.ParseNonExecutionResponse(
                """
                {"result":{"sandboxId":"iso:abc123",
                "metadata":{"agentUserName":"MxcAgent_1"}}}
                """));

        Assert.Null(result.Metadata);
    }

    // The envelopes below are verbatim captures from @microsoft/mxc-sdk 0.8.0
    // wxc-exec.exe running against the Windows IsolationSession backend, so
    // they pin the real contract rather than an assumed one.

    [Theory]
    [InlineData("malformed_id", nameof(MxcErrorCode.MalformedId))]
    [InlineData("stale_id", nameof(MxcErrorCode.StaleId))]
    [InlineData("policy_validation", nameof(MxcErrorCode.PolicyValidation))]
    [InlineData("malformed_request", nameof(MxcErrorCode.MalformedRequest))]
    [InlineData("unsupported_containment", nameof(MxcErrorCode.UnsupportedContainment))]
    [InlineData("backend_error", nameof(MxcErrorCode.BackendError))]
    [InlineData("some_future_code", nameof(MxcErrorCode.Unknown))]
    public void ObservedBackendCodesAreClassified(
        string backendCode,
        string expectedName)
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.ParseNonExecutionResponse(
                $"{{\"error\":{{\"code\":\"{backendCode}\",\"message\":\"m\"}}}}"));

        Assert.Equal(Enum.Parse<MxcErrorCode>(expectedName), exception.Code);

        // The verbatim code survives classification, including when this
        // package has no specific handling for it.
        Assert.Equal(backendCode, exception.BackendCode);
    }

    [Fact]
    public void ExecutionAgainstStoppedSessionSurfacesBackendRemediation()
    {
        // captured from an exec issued after a successful stop.
        const string envelope = """
            {"error":{"code":"backend_error",
            "message":"No active session exists.",
            "operation":"IsoSessionOps.RunProcessWithOptionsAsync",
            "nativeCode":"0x80070520",
            "remediation":"No active session found for this agent user. Start a session first, then retry."}}
            """;

        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.ParseNonExecutionResponse(envelope));

        Assert.Equal(MxcErrorCode.BackendError, exception.Code);

        // Operators need the backend's own recovery step and native status,
        // not just the one-line summary.
        Assert.Contains("No active session exists.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Start a session first", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0x80070520", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisionResultReadsRealIsolationSessionPayload()
    {
        // captured from a real provision on Windows build 26686.1000.
        const string envelope = """
            {"result":{"metadata":{"agentUserName":"C8-H2",
            "agentUserSid":"S-1-5-21-3955704215-4282272831-1814204317-1321",
            "ephemeralWorkspacePath":"C:\\Users\\C8-H2\\Shared"},
            "sandboxId":"iso:eyJ2ZXJzaW9uIjoxfQ"}}
            """;

        MxcProvisionResult result = MxcWireProtocol.ReadProvisionResult(
            MxcWireProtocol.ParseNonExecutionResponse(envelope));

        Assert.Equal("iso:eyJ2ZXJzaW9uIjoxfQ", result.SandboxId.Value);
        Assert.NotNull(result.Metadata);
        Assert.Equal("C8-H2", result.Metadata!.AgentUserName);
        Assert.Equal(
            @"C:\Users\C8-H2\Shared",
            result.Metadata.EphemeralWorkspacePath);
    }

    [Fact]
    public void ProbeResponseIsReadFromTheCapturedRuntimeOutput()
    {
        // Verbatim `wxc-exec --probe` output captured from the pinned runtime
        // on a capable host; see docs\mxc-compatibility-evidence.md (G5).
        const string captured = """
            {"tier":"base-container","needsDaclAugmentation":false,"warnings":[],"probes":{"baseContainerApiPresent":true,"isolationSessionAvailable":true}}
            """;

        MxcBackendProbe probe = MxcWireProtocol.ReadProbeResponse(captured);

        Assert.True(probe.IsolationSessionAvailable);
        Assert.Equal("base-container", probe.Tier);
        Assert.Empty(probe.Warnings);
    }

    [Fact]
    public void ProbeResponseCarriesWarningsAndAnUnavailableBackend()
    {
        const string payload = """
            {"tier":"none","warnings":["host preparation required"],"probes":{"isolationSessionAvailable":false}}
            """;

        MxcBackendProbe probe = MxcWireProtocol.ReadProbeResponse(payload);

        Assert.False(probe.IsolationSessionAvailable);
        Assert.Equal("none", probe.Tier);
        Assert.Equal("host preparation required", Assert.Single(probe.Warnings));
    }

    [Fact]
    public void AProbeResponseMissingTheAvailabilityFlagIsRejected()
    {
        // Defaulting a missing flag either way would silently invent a support
        // verdict, so the absent field must surface as a protocol violation.
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.ReadProbeResponse("""{"tier":"base-container"}"""));

        Assert.Equal(MxcErrorCode.ProtocolViolation, exception.Code);
    }

    [Fact]
    public void AMalformedProbeResponseIsRejected()
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.ReadProbeResponse("not json"));

        Assert.Equal(MxcErrorCode.ProtocolViolation, exception.Code);
    }

    private static JsonElement Decode(string configBase64) =>
        JsonDocument
            .Parse(Encoding.UTF8.GetString(Convert.FromBase64String(configBase64)))
            .RootElement;
}
