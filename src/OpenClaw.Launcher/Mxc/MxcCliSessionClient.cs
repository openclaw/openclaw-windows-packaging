using System.Diagnostics;
using System.Text;

namespace OpenClaw.Launcher.Mxc;

public sealed record MxcExecutorInvocation(
    string ExecutorPath,
    IReadOnlyList<string> Arguments);

public sealed record MxcExecutorOutcome(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Runs the MXC executor. Exists so lifecycle behavior can be tested without a
/// real sandbox, a capable host, or executing a downloaded binary.
/// </summary>
public interface IMxcExecutorInvoker
{
    Task<MxcExecutorOutcome> InvokeAsync(
        MxcExecutorInvocation invocation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Temporary <see cref="IMxcSessionClient"/> transport over the executor
/// published in <c>@microsoft/mxc-sdk</c>.
/// </summary>
/// <remarks>
/// This exists only until the official Microsoft.Mxc.Sdk .NET package ships.
/// Every preview wire detail is confined to this adapter and
/// <see cref="MxcWireProtocol"/>; the published SDK adapter must satisfy the
/// same <see cref="IMxcSessionClient"/> behavior.
/// </remarks>
public sealed class MxcCliSessionClient : IMxcSessionClient
{
    private readonly MxcRuntimeLocation _runtime;
    private readonly IMxcExecutorInvoker _invoker;

    public MxcCliSessionClient(
        MxcRuntimeLocation runtime,
        IMxcExecutorInvoker? invoker = null)
    {
        _runtime = runtime;
        _invoker = invoker ?? new ProcessMxcExecutorInvoker();
    }

    public async Task<MxcProvisionResult> ProvisionAsync(
        MxcProvisionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.AppId))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                "A packaged caller must supply PFN:<packageFamilyName>.");
        }

        MxcExecutorOutcome outcome = await InvokeAsync(
            MxcWireProtocol.BuildProvisionEnvelope(request.AppId),
            cancellationToken);
        return MxcWireProtocol.ReadProvisionResult(ReadResult(outcome));
    }

    public async Task StartAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        ReadResult(await InvokeAsync(
            MxcWireProtocol.BuildPhaseEnvelope(
                MxcWireProtocol.StartPhase,
                sandboxId,
                correlationVector),
            cancellationToken));

    public async Task<MxcExecutionResult> ExecuteAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        string? correlationVector,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CommandLine))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                "An execution request requires a command line.");
        }

        MxcExecutorOutcome outcome = await InvokeAsync(
            MxcWireProtocol.BuildPhaseEnvelope(
                MxcWireProtocol.ExecPhase,
                sandboxId,
                correlationVector,
                request.CommandLine),
            cancellationToken);

        // Execution forwards the guest command's own output and exit code. A
        // dispatch failure is only claimed when the executor also failed, so a
        // command that legitimately prints an error-shaped JSON document is
        // reported as command output rather than an MXC fault.
        if (outcome.ExitCode != 0)
        {
            MxcException? dispatchFailure =
                MxcWireProtocol.TryParseExecutionError(outcome.StandardOutput);
            if (dispatchFailure is not null)
            {
                throw dispatchFailure;
            }
        }

        return new MxcExecutionResult(
            outcome.ExitCode,
            outcome.StandardOutput,
            outcome.StandardError);
    }

    public async Task StopAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        ReadResult(await InvokeAsync(
            MxcWireProtocol.BuildPhaseEnvelope(
                MxcWireProtocol.StopPhase,
                sandboxId,
                correlationVector),
            cancellationToken));

    public async Task DeprovisionAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        ReadResult(await InvokeAsync(
            MxcWireProtocol.BuildPhaseEnvelope(
                MxcWireProtocol.DeprovisionPhase,
                sandboxId,
                correlationVector),
            cancellationToken));

    /// <summary>
    /// Runs the executor's host capability detector. This is the authoritative
    /// answer to whether the IsolationSession backend is usable here; the
    /// documented minimum Windows build only predicts it. The detector does not
    /// spawn a sandbox, so this is safe on the read-only setup path.
    /// </summary>
    public async Task<MxcBackendProbe> ProbeBackendAsync(
        CancellationToken cancellationToken)
    {
        MxcExecutorOutcome outcome = await _invoker.InvokeAsync(
            new MxcExecutorInvocation(_runtime.ExecutorPath, ["--probe"]),
            cancellationToken).ConfigureAwait(false);

        if (outcome.ExitCode != 0 || string.IsNullOrWhiteSpace(outcome.StandardOutput))
        {
            throw new MxcException(
                MxcErrorCode.RuntimeUnavailable,
                "The MXC host capability probe failed " +
                $"(exit code {outcome.ExitCode}). " +
                Describe(outcome.StandardError));
        }

        return MxcWireProtocol.ReadProbeResponse(outcome.StandardOutput);
    }

    private Task<MxcExecutorOutcome> InvokeAsync(
        MxcRequestEnvelope envelope,
        CancellationToken cancellationToken) =>
        _invoker.InvokeAsync(
            new MxcExecutorInvocation(
                _runtime.ExecutorPath,
                [
                    "--config-base64",
                    MxcWireProtocol.EncodeConfig(envelope),

                    // The state-aware lifecycle surface is gated behind this
                    // flag in the pinned runtime; without it the executor
                    // rejects the request before reading the envelope.
                    "--experimental"
                ]),
            cancellationToken);

    private static System.Text.Json.JsonElement ReadResult(
        MxcExecutorOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.StandardOutput))
        {
            throw new MxcException(
                MxcErrorCode.ProtocolViolation,
                "The MXC executor produced no response envelope " +
                $"(exit code {outcome.ExitCode}). " +
                Describe(outcome.StandardError));
        }

        // Parse before inspecting the exit code: a structured {error} envelope
        // names the real failure, and reporting the exit code instead would
        // discard it.
        return MxcWireProtocol.ParseNonExecutionResponse(outcome.StandardOutput);
    }

    private static string Describe(string standardError) =>
        string.IsNullOrWhiteSpace(standardError)
            ? "The executor reported no diagnostics."
            : $"Executor diagnostics: {standardError.Trim()}";
}

internal sealed class ProcessMxcExecutorInvoker : IMxcExecutorInvoker
{
    public async Task<MxcExecutorOutcome> InvokeAsync(
        MxcExecutorInvocation invocation,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = invocation.ExecutorPath,

            // No shell: the executor path and the base64 envelope must reach
            // the process exactly as written.
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            InvalidOperationException)
        {
            throw new MxcException(
                MxcErrorCode.RuntimeUnavailable,
                $"The MXC executor could not be started: {exception.Message}",
                innerException: exception);
        }

        // Both streams are read concurrently so a large payload on one cannot
        // fill its pipe buffer and deadlock the child before exit.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new MxcExecutorOutcome(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }
}
