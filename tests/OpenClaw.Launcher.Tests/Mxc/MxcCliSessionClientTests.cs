using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcCliSessionClientTests
{
    private static readonly MxcRuntimeLocation Runtime = new(
        @"C:\package\mxc\x64",
        @"C:\package\mxc\x64\wxc-exec.exe",
        @"C:\package\mxc\x64\plm.exe",
        new MxcRuntimeProvenance("@microsoft/mxc-sdk", "0.8.0", "x64"));

    private static readonly MxcSandboxId SandboxId = MxcSandboxId.Parse("iso:abc");

    [Fact]
    public async Task ProvisionInvokesTheStagedExecutorWithAnEncodedEnvelope()
    {
        var invoker = new RecordingInvoker(
            """{"result":{"sandboxId":"iso:abc","correlationVector":"cv-1"}}""");
        var client = new MxcCliSessionClient(Runtime, invoker);

        MxcProvisionResult result = await client.ProvisionAsync(
            new MxcProvisionRequest("PFN:Contoso.App_8wekyb3d8bbwe"),
            CancellationToken.None);

        Assert.Equal("iso:abc", result.SandboxId.Value);
        Assert.Equal(Runtime.ExecutorPath, invoker.Invocation!.ExecutorPath);
        Assert.Equal("--config-base64", invoker.Invocation.Arguments[0]);

        // The state-aware lifecycle surface is gated behind this flag; without
        // it the executor rejects the request before reading the envelope.
        Assert.Contains("--experimental", invoker.Invocation.Arguments);

        MxcRequestEnvelope sent =
            MxcWireProtocol.DecodeConfig(invoker.Invocation.Arguments[1]);
        Assert.Equal(MxcWireProtocol.ProvisionPhase, sent.Phase);
        Assert.Equal(
            "PFN:Contoso.App_8wekyb3d8bbwe",
            sent.Experimental?.IsolationSession?.Provision?.AppId);
    }

    [Fact]
    public async Task ProvisionWithoutAnApplicationIdentityIsRejectedLocally()
    {
        var invoker = new RecordingInvoker("{}");
        var client = new MxcCliSessionClient(Runtime, invoker);

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => client.ProvisionAsync(
                new MxcProvisionRequest(string.Empty),
                CancellationToken.None));

        Assert.Equal(MxcErrorCode.PolicyValidation, exception.Code);
        Assert.Null(invoker.Invocation);
    }

    [Theory]
    [InlineData(MxcWireProtocol.StartPhase)]
    [InlineData(MxcWireProtocol.StopPhase)]
    [InlineData(MxcWireProtocol.DeprovisionPhase)]
    public async Task LifecyclePhasesReplayTheSandboxIdentityAndCorrelation(
        string phase)
    {
        var invoker = new RecordingInvoker("""{"result":{}}""");
        var client = new MxcCliSessionClient(Runtime, invoker);

        await (phase switch
        {
            MxcWireProtocol.StartPhase =>
                client.StartAsync(SandboxId, "cv-9", CancellationToken.None),
            MxcWireProtocol.StopPhase =>
                client.StopAsync(SandboxId, "cv-9", CancellationToken.None),
            _ => client.DeprovisionAsync(
                SandboxId,
                "cv-9",
                CancellationToken.None)
        }).ConfigureAwait(true);

        MxcRequestEnvelope sent =
            MxcWireProtocol.DecodeConfig(invoker.Invocation!.Arguments[1]);
        Assert.Equal(phase, sent.Phase);
        Assert.Equal(SandboxId.Value, sent.SandboxId);
        Assert.Equal("cv-9", sent.CorrelationVector);
    }

    [Fact]
    public async Task LifecycleFailureReportsTheBackendErrorNotTheExitCode()
    {
        var client = new MxcCliSessionClient(
            Runtime,
            new RecordingInvoker(
                """{"error":{"code":"stale_id","message":"sandbox is gone"}}""",
                exitCode: 1));

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => client.StartAsync(SandboxId, null, CancellationToken.None));

        Assert.Equal(MxcErrorCode.StaleId, exception.Code);
        Assert.Equal("sandbox is gone", exception.Message);
    }

    [Fact]
    public async Task SilentExecutorFailureSurfacesItsDiagnostics()
    {
        var client = new MxcCliSessionClient(
            Runtime,
            new RecordingInvoker(
                standardOutput: string.Empty,
                exitCode: 9,
                standardError: "backend unavailable"));

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => client.StopAsync(SandboxId, null, CancellationToken.None));

        Assert.Equal(MxcErrorCode.ProtocolViolation, exception.Code);
        Assert.Contains("backend unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("9", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutionForwardsTheCommandsOwnOutputAndExitCode()
    {
        var client = new MxcCliSessionClient(
            Runtime,
            new RecordingInvoker(
                standardOutput: "openclaw output",
                exitCode: 3,
                standardError: "openclaw warning"));

        MxcExecutionResult result = await client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("\"C:\\helper.exe\" --request r.json"),
            null,
            CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("openclaw output", result.StandardOutput);
        Assert.Equal("openclaw warning", result.StandardError);
    }

    [Fact]
    public async Task FailingCommandThatPrintsErrorShapedJsonIsNotADispatchFailure()
    {
        // OpenClaw legitimately emits JSON. Treating it as an MXC fault would
        // replace a real command failure with a misleading session error.
        var client = new MxcCliSessionClient(
            Runtime,
            new RecordingInvoker(
                standardOutput: """{"error":{"reason":"login required"}}""",
                exitCode: 1));

        MxcExecutionResult result = await client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            null,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("login required", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchFailureDuringExecutionIsReportedAsASessionError()
    {
        var client = new MxcCliSessionClient(
            Runtime,
            new RecordingInvoker(
                standardOutput:
                    """{"error":{"code":"stale_id","message":"sandbox is gone"}}""",
                exitCode: 1));

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => client.ExecuteAsync(
                SandboxId,
                new MxcExecutionRequest("helper.exe"),
                null,
                CancellationToken.None));

        Assert.Equal(MxcErrorCode.StaleId, exception.Code);
    }

    [Fact]
    public async Task SuccessfulCommandOutputIsNeverInspectedForDispatchErrors()
    {
        var client = new MxcCliSessionClient(
            Runtime,
            new RecordingInvoker(
                standardOutput:
                    """{"error":{"code":"stale_id","message":"printed by the app"}}""",
                exitCode: 0));

        MxcExecutionResult result = await client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            null,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecutionWithoutACommandLineIsRejectedLocally()
    {
        var invoker = new RecordingInvoker("{}");
        var client = new MxcCliSessionClient(Runtime, invoker);

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => client.ExecuteAsync(
                SandboxId,
                new MxcExecutionRequest("   "),
                null,
                CancellationToken.None));

        Assert.Equal(MxcErrorCode.PolicyValidation, exception.Code);
        Assert.Null(invoker.Invocation);
    }

    [Fact]
    public async Task ProbeAsksTheExecutorWithoutAnEnvelopeOrSandbox()
    {
        // The probe must stay non-mutating: no config envelope, no
        // experimental lifecycle flag, and therefore no sandbox is created on
        // the read-only setup path.
        var invoker = new RecordingInvoker(
            """{"tier":"base-container","warnings":[],"probes":{"isolationSessionAvailable":true}}""");
        var client = new MxcCliSessionClient(Runtime, invoker);

        MxcBackendProbe probe = await client.ProbeBackendAsync(
            CancellationToken.None);

        Assert.True(probe.IsolationSessionAvailable);
        Assert.Equal(Runtime.ExecutorPath, invoker.Invocation!.ExecutorPath);
        Assert.Equal(["--probe"], invoker.Invocation.Arguments);
    }

    [Fact]
    public async Task AFailedProbeInvocationSurfacesAsRuntimeUnavailable()
    {
        var invoker = new RecordingInvoker(
            standardOutput: string.Empty,
            exitCode: 1,
            standardError: "probe unsupported");
        var client = new MxcCliSessionClient(Runtime, invoker);

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => client.ProbeBackendAsync(CancellationToken.None));

        Assert.Equal(MxcErrorCode.RuntimeUnavailable, exception.Code);
        Assert.Contains("probe unsupported", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingInvoker(
        string standardOutput,
        int exitCode = 0,
        string standardError = "") : IMxcExecutorInvoker
    {
        public MxcExecutorInvocation? Invocation { get; private set; }

        public Task<MxcExecutorOutcome> InvokeAsync(
            MxcExecutorInvocation invocation,
            CancellationToken cancellationToken)
        {
            Invocation = invocation;
            return Task.FromResult(
                new MxcExecutorOutcome(exitCode, standardOutput, standardError));
        }
    }
}
