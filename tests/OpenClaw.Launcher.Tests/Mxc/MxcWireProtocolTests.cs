using System.Text;
using System.Text.Json;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcWireProtocolTests
{
    private const string SandboxIdValue = "iso:abc123";

    [Fact]
    public void ProvisionRequestUsesTheStableV1ConfigAndCliOperation()
    {
        MxcCliRequest request =
            MxcWireProtocol.BuildProvisionRequest("PFN:Contoso.App_8wekyb3d8bbwe");

        JsonElement encoded = Decode(MxcWireProtocol.EncodeConfig(request.Config));

        Assert.Equal(
            "1.0.0",
            encoded.GetProperty("version").GetString());
        Assert.Equal("provision", request.Operation);
        Assert.Null(request.ContainerId);
        Assert.Equal(
            "isolation_session",
            encoded.GetProperty("containment").GetString());
        Assert.Equal(
            "allow",
            encoded.GetProperty("network").GetProperty("egress")
                .GetProperty("default").GetString());
        Assert.Equal(
            "allow",
            encoded.GetProperty("network").GetProperty("ingress")
                .GetProperty("default").GetString());
        Assert.Equal(
            "allow",
            encoded.GetProperty("network").GetProperty("ingress")
                .GetProperty("hostLoopback").GetString());

        Assert.Equal(
            "PFN:Contoso.App_8wekyb3d8bbwe",
            encoded.GetProperty("isolationSession")
                .GetProperty("provision")
                .GetProperty("appId")
                .GetString());
        Assert.False(encoded.TryGetProperty("appId", out _));
        Assert.False(encoded.TryGetProperty("phase", out _));
        Assert.False(encoded.TryGetProperty("sandboxId", out _));
        Assert.False(encoded.TryGetProperty("experimental", out _));

        IReadOnlyList<string> arguments = MxcWireProtocol.BuildArguments(request);
        Assert.Equal("--config-base64", arguments[0]);
        Assert.Equal("--operation", arguments[2]);
        Assert.Equal("provision", arguments[3]);
        Assert.DoesNotContain("--container-id", arguments);
        Assert.DoesNotContain("--experimental", arguments);
    }

    [Fact]
    public void PostProvisionRequestCarriesRoutingOnlyInCliArguments()
    {
        MxcCliRequest request = MxcWireProtocol.BuildPhaseRequest(
            MxcWireProtocol.ExecPhase,
            MxcSandboxId.Parse(SandboxIdValue),
            "\"C:\\Program Files\\helper.exe\" --request r.json");

        JsonElement encoded = Decode(MxcWireProtocol.EncodeConfig(request.Config));

        Assert.Equal("1.0.0", encoded.GetProperty("version").GetString());
        Assert.Equal(
            "\"C:\\Program Files\\helper.exe\" --request r.json",
            encoded.GetProperty("process").GetProperty("commandLine").GetString());

        Assert.False(encoded.TryGetProperty("phase", out _));
        Assert.False(encoded.TryGetProperty("sandboxId", out _));
        Assert.False(encoded.TryGetProperty("correlationVector", out _));
        Assert.False(encoded.TryGetProperty("network", out _));
        Assert.False(encoded.TryGetProperty("containment", out _));
        Assert.False(encoded.TryGetProperty("isolationSession", out _));

        IReadOnlyList<string> arguments = MxcWireProtocol.BuildArguments(request);
        Assert.Equal("--operation", arguments[2]);
        Assert.Equal("exec", arguments[3]);
        Assert.Equal("--container-id", arguments[4]);
        Assert.Equal(SandboxIdValue, arguments[5]);
    }

    [Fact]
    public void LifecycleRequestWithoutCommandOmitsProcessAndKeepsContainerIdInCli()
    {
        MxcCliRequest request = MxcWireProtocol.BuildPhaseRequest(
            MxcWireProtocol.StopPhase,
            MxcSandboxId.Parse(SandboxIdValue));
        JsonElement encoded = Decode(MxcWireProtocol.EncodeConfig(request.Config));

        Assert.False(encoded.TryGetProperty("process", out _));
        Assert.False(encoded.TryGetProperty("phase", out _));
        Assert.False(encoded.TryGetProperty("sandboxId", out _));
        IReadOnlyList<string> arguments = MxcWireProtocol.BuildArguments(request);
        Assert.Equal("stop", arguments[3]);
        Assert.Equal(SandboxIdValue, arguments[5]);
    }

    [Fact]
    public void PhaseRequestRejectsAnotherBackendsSandboxId()
    {
        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.BuildPhaseRequest(
                MxcWireProtocol.StartPhase,
                MxcSandboxId.Parse("wsb:abc123")));

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

    [Fact]
    public void ProvisionResultReportsIdentityAndWorkspaceMetadata()
    {
        MxcProvisionResult result = MxcWireProtocol.ReadProvisionResult(
            MxcWireProtocol.ParseNonExecutionResponse(
                """
                {"result":{"sandboxId":"iso:abc123",
                "metadata":{"agentUserName":"MxcAgent_1","agentUserSid":"S-1-5-21-1",
                "ephemeralWorkspacePath":"C:\\ws\\1"}}}
                """));

        Assert.Equal("iso:abc123", result.SandboxId.Value);
        Assert.True(result.SandboxId.IsIsolationSession);
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
                    """{"result":{}}""")));

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
    public void LifecycleFailurePreservesBackendRemediationAndOperation()
    {
        const string envelope = """
            {"error":{"code":"backend_error",
            "message":"The session could not be started.",
            "operation":"IsoSessionOps.StartSessionAsync",
            "nativeCode":"0x80070520",
            "remediation":"Start the session from an interactive session, then retry."}}
            """;

        MxcException exception = Assert.Throws<MxcException>(
            () => MxcWireProtocol.ParseNonExecutionResponse(envelope));

        Assert.Equal(MxcErrorCode.BackendError, exception.Code);

        // Operators need the backend's own recovery step and native status,
        // not just the one-line summary.
        Assert.Contains("could not be started", exception.Message, StringComparison.Ordinal);
        Assert.Contains("interactive session", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0x80070520", exception.Message, StringComparison.Ordinal);

        // The failing OS call is what distinguishes, say, a refused start from
        // a refused exec when only the log is available.
        Assert.Equal("IsoSessionOps.StartSessionAsync", exception.Operation);
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
