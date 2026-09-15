using System.Diagnostics;
using System.Text;

namespace OpenClaw.Launcher.Mxc;

internal sealed record MxcExecutorInvocation(
    string ExecutorPath,
    IReadOnlyList<string> Arguments);

internal sealed record MxcExecutorOutcome(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Runs the MXC executor. Exists so lifecycle behavior can be tested without a
/// real sandbox, a capable host, or executing a downloaded binary.
/// </summary>
internal interface IMxcExecutorInvoker
{
    Task<MxcExecutorOutcome> InvokeAsync(
        MxcExecutorInvocation invocation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs the MXC executor with the host's console streams attached.
/// </summary>
/// <remarks>
/// Interactive OpenClaw cannot be served by the buffered invoker: reading both
/// streams to completion only returns after the child exits, so a prompt would
/// never reach the terminal and typed input would never reach the child.
/// Nothing is captured here, so the caller cannot read a dispatch error
/// envelope off standard output and must establish the outcome another way.
/// </remarks>
internal interface IMxcAttachedExecutorInvoker
{
    Task<int> InvokeAttachedAsync(
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
internal sealed class MxcCliSessionClient : IMxcSessionClient
{
    private readonly MxcRuntimeLocation _runtime;
    private readonly IMxcExecutorInvoker _invoker;
    private readonly IMxcAttachedExecutorInvoker _attachedInvoker;

    public MxcCliSessionClient(
        MxcRuntimeLocation runtime,
        IMxcExecutorInvoker? invoker = null,
        IMxcAttachedExecutorInvoker? attachedInvoker = null)
    {
        _runtime = runtime;
        _invoker = invoker ?? new ProcessMxcExecutorInvoker();
        _attachedInvoker = attachedInvoker ??
            invoker as IMxcAttachedExecutorInvoker ??
            new ProcessMxcExecutorInvoker();
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
            cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false));

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
            cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Runs a command with the host's console streams attached.
    /// </summary>
    /// <remarks>
    /// Nothing is captured, so a dispatch failure cannot be read back as a
    /// structured envelope and would instead print to the user's terminal. The
    /// caller establishes the real outcome from the guest helper's control
    /// result, which distinguishes "the application exited with this code" from
    /// "the command never started".
    /// </remarks>
    public Task<int> ExecuteAttachedAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        _attachedInvoker.InvokeAttachedAsync(
            BuildInvocation(MxcWireProtocol.BuildPhaseEnvelope(
                MxcWireProtocol.ExecPhase,
                sandboxId,
                correlationVector,
                request.CommandLine)),
            cancellationToken);

    public async Task StopAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        ReadResult(await InvokeAsync(
            MxcWireProtocol.BuildPhaseEnvelope(
                MxcWireProtocol.StopPhase,
                sandboxId,
                correlationVector),
            cancellationToken).ConfigureAwait(false));

    public async Task DeprovisionAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        ReadResult(await InvokeAsync(
            MxcWireProtocol.BuildPhaseEnvelope(
                MxcWireProtocol.DeprovisionPhase,
                sandboxId,
                correlationVector),
            cancellationToken).ConfigureAwait(false));

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
        _invoker.InvokeAsync(BuildInvocation(envelope), cancellationToken);

    private MxcExecutorInvocation BuildInvocation(MxcRequestEnvelope envelope) =>
        new(
            _runtime.ExecutorPath,
            [
                "--config-base64",
                MxcWireProtocol.EncodeConfig(envelope),

                // The state-aware lifecycle surface is gated behind this
                // flag in the pinned runtime; without it the executor
                // rejects the request before reading the envelope.
                "--experimental"
            ]);

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

internal sealed class ProcessMxcExecutorInvoker
    : IMxcExecutorInvoker, IMxcAttachedExecutorInvoker
{
    private readonly IHostConsole _console;

    internal ProcessMxcExecutorInvoker(IHostConsole? console = null) =>
        _console = console ?? WindowsHostConsole.Instance;

    public async Task<MxcExecutorOutcome> InvokeAsync(
        MxcExecutorInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = new()
        {
            FileName = invocation.ExecutorPath,

            // No shell: the executor path and the base64 envelope must reach
            // the process exactly as written.
            UseShellExecute = false,
            RedirectStandardInput = true,
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
            process.StandardInput.Close();
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
        using CancellationTokenRegistration registration =
            cancellationToken.Register(() => TryKill(process));
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new MxcExecutorOutcome(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    public async Task<int> InvokeAttachedAsync(
        MxcExecutorInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable capture = _console.Capture(_ => { });
        _console.InitializeUtf8();
        ProcessStartInfo startInfo = new()
        {
            FileName = invocation.ExecutorPath,

            // Nothing is redirected, so the child inherits this process's
            // console handles. That is the point: OpenClaw draws its own
            // prompts and reads typed input, and any interposed pipe would
            // both buffer that output and hide the terminal from the child.
            UseShellExecute = false
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

        // Only this invocation's process tree is killed. The session itself
        // outlives the command, and other OpenClaw invocations own their own
        // executor processes. Awaiting without the cancelled token guarantees
        // the process is gone before cancellation is returned to the caller.
        using CancellationTokenRegistration registration =
            cancellationToken.Register(() => TryKill(process));
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return process.ExitCode;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
        }
    }
}
