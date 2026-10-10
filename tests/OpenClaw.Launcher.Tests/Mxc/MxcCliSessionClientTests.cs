using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcCliSessionClientTests
{
    private static readonly MxcRuntimeLocation Runtime = new(
        @"C:\package\mxc\x64",
        @"C:\package\mxc\x64\wxc-exec.exe",
        new MxcRuntimeProvenance("@microsoft/mxc-sdk", "1.0.0", "x64"));

    private static readonly MxcSandboxId SandboxId = MxcSandboxId.Parse("iso:abc");

    [Fact]
    public async Task ProvisionInvokesTheExecutorWithTheStableCliContract()
    {
        var invoker = new RecordingInvoker(
            """{"result":{"sandboxId":"iso:abc"}}""");
        var client = new MxcCliSessionClient(Runtime, invoker);

        MxcProvisionResult result = await client.ProvisionAsync(
            new MxcProvisionRequest("PFN:Contoso.App_8wekyb3d8bbwe"),
            CancellationToken.None);

        Assert.Equal("iso:abc", result.SandboxId.Value);
        Assert.Equal(Runtime.ExecutorPath, invoker.Invocation!.ExecutorPath);
        Assert.Equal("--config-base64", invoker.Invocation.Arguments[0]);
        Assert.Equal("--operation", invoker.Invocation.Arguments[2]);
        Assert.Equal("provision", invoker.Invocation.Arguments[3]);
        Assert.DoesNotContain("--container-id", invoker.Invocation.Arguments);
        Assert.DoesNotContain("--experimental", invoker.Invocation.Arguments);

        MxcRequestEnvelope sent =
            MxcWireProtocol.DecodeConfig(invoker.Invocation.Arguments[1]);
        Assert.Equal("1.0.0", sent.Version);
        Assert.Equal("isolation_session", sent.Containment);
        Assert.Equal(
            "PFN:Contoso.App_8wekyb3d8bbwe",
            sent.IsolationSession?.Provision?.AppId);
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
    public async Task LifecycleOperationsUseCliRouting(
        string phase)
    {
        var invoker = new RecordingInvoker("""{"result":{}}""");
        var client = new MxcCliSessionClient(Runtime, invoker);

        await (phase switch
        {
            MxcWireProtocol.StartPhase =>
                client.StartAsync(SandboxId, CancellationToken.None),
            MxcWireProtocol.StopPhase =>
                client.StopAsync(SandboxId, CancellationToken.None),
            _ => client.DeprovisionAsync(
                SandboxId,
                CancellationToken.None)
        }).ConfigureAwait(true);

        MxcRequestEnvelope sent =
            MxcWireProtocol.DecodeConfig(invoker.Invocation!.Arguments[1]);
        Assert.Equal("--operation", invoker.Invocation.Arguments[2]);
        Assert.Equal(phase, invoker.Invocation.Arguments[3]);
        Assert.Equal("--container-id", invoker.Invocation.Arguments[4]);
        Assert.Equal(SandboxId.Value, invoker.Invocation.Arguments[5]);
        Assert.Equal("1.0.0", sent.Version);
        Assert.Null(sent.Containment);
        Assert.Null(sent.Network);
        Assert.Null(sent.IsolationSession);
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
            () => client.StartAsync(SandboxId, CancellationToken.None));

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
            () => client.StopAsync(SandboxId, CancellationToken.None));

        Assert.Equal(MxcErrorCode.ProtocolViolation, exception.Code);
        Assert.Contains("backend unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("9", exception.Message, StringComparison.Ordinal);
    }

    // An executor that crashes mid-response leaves no envelope to name the
    // failure; its exit code, diagnostics, and raw output are all that remain.
    [Fact]
    public async Task UninterpretableExecutorOutputKeepsTheEvidenceOfWhatHappened()
    {
        var client = new MxcCliSessionClient(
            Runtime,
            new RecordingInvoker(
                standardOutput: "thread 'main' panicked at isolation_session.rs",
                exitCode: -1073740791,
                standardError: "fatal runtime error"));

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => client.StartAsync(SandboxId, CancellationToken.None));

        Assert.Equal(MxcErrorCode.ProtocolViolation, exception.Code);
        Assert.Contains("(exit code -1073740791)", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "Executor diagnostics: fatal runtime error",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Executor output: thread 'main' panicked at isolation_session.rs",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunawayExecutorOutputIsBoundedInTheFailure()
    {
        string output = new('x', 10_000);
        var client = new MxcCliSessionClient(
            Runtime,
            new RecordingInvoker(standardOutput: output, exitCode: 1));

        MxcException exception = await Assert.ThrowsAsync<MxcException>(
            () => client.StartAsync(SandboxId, CancellationToken.None));

        Assert.Contains("... (10000 characters)", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Message.Length < 1_000, exception.Message);
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
            CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("openclaw output", result.StandardOutput);
        Assert.Equal("openclaw warning", result.StandardError);
    }

    [Fact]
    public async Task ExecutionReturnsJsonLookingGuestOutputOnBothStreamsUnchanged()
    {
        const string stdout = """{"error":{"reason":"guest output"}}""";
        const string stderr = """{"error":{"reason":"guest warning"}}""";
        var client = new MxcCliSessionClient(
            Runtime,
            new RecordingInvoker(
                standardOutput: stdout,
                exitCode: 1,
                standardError: stderr));

        MxcExecutionResult result = await client.ExecuteAsync(
            SandboxId,
            new MxcExecutionRequest("helper.exe"),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(stdout, result.StandardOutput);
        Assert.Equal(stderr, result.StandardError);
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
                CancellationToken.None));

        Assert.Equal(MxcErrorCode.PolicyValidation, exception.Code);
        Assert.Null(invoker.Invocation);
    }

    [Fact]
    public async Task ProbeAsksTheExecutorWithoutAnEnvelopeOrSandbox()
    {
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
