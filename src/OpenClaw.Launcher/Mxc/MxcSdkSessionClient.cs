using Sdk = Microsoft.Mxc.Sdk;

namespace OpenClaw.Launcher.Mxc;

/// <summary>
/// The host's standard streams as the attached execution path sees them.
/// </summary>
internal interface IMxcHostStdio
{
    /// <summary>
    /// Whether standard input and standard output are both console handles,
    /// which the SDK requires before it attaches a workload to this process.
    /// </summary>
    bool InputAndOutputAreConsoles { get; }

    Stream OpenInput();

    Stream OpenOutput();

    Stream OpenError();
}

internal sealed class ProcessMxcHostStdio : IMxcHostStdio
{
    public static ProcessMxcHostStdio Instance { get; } = new();

    public bool InputAndOutputAreConsoles =>
        WindowsHostConsole.Instance.HasConsoleInputAndOutput;

    public Stream OpenInput() => Console.OpenStandardInput();

    public Stream OpenOutput() => Console.OpenStandardOutput();

    public Stream OpenError() => Console.OpenStandardError();
}

/// <summary>
/// <see cref="IMxcSessionClient"/> over the in-process <c>Microsoft.Mxc.Sdk</c>
/// state-aware lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Every SDK type and failure is translated here, so the rest of the launcher
/// keeps depending on the project-owned session contract and error
/// classification.
/// </para>
/// <para>
/// The SDK's native calls block and cannot be abandoned once dispatched. A
/// cancellation that arrives after dispatch is therefore not observed for
/// lifecycle phases: abandoning a provision would lose a sandbox the backend
/// had already created.
/// </para>
/// </remarks>
internal sealed class MxcSdkSessionClient : IMxcSessionClient
{
    private const int RelayBufferSize = 81920;

    private readonly MxcRuntimeLocation _runtime;
    private readonly Sdk.ISandboxLifecycle _lifecycle;
    private readonly IHostConsole _console;
    private readonly IMxcHostStdio _stdio;

    public MxcSdkSessionClient(
        MxcRuntimeLocation runtime,
        Sdk.ISandboxLifecycle? lifecycle = null,
        IHostConsole? console = null,
        IMxcHostStdio? stdio = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _lifecycle = lifecycle ?? Sdk.MxcSandboxLifecycle.Default;
        _console = console ?? WindowsHostConsole.Instance;
        _stdio = stdio ?? ProcessMxcHostStdio.Instance;
    }

    public Task<MxcProvisionResult> ProvisionAsync(
        MxcProvisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.AppId))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                "A packaged caller must supply PFN:<packageFamilyName>.");
        }

        var options = CreateProvisionOptions(request.AppId);

        return InvokeAsync(
            () => ToProvisionResult(_lifecycle.ProvisionSandbox(
                Sdk.StateAwareContainment.IsolationSession,
                options)),
            cancellationToken);
    }

    /// <summary>
    /// Builds the provision request for this package's session. IsolationSession
    /// runs on a network MXC cannot filter, so the only posture it accepts is
    /// the explicit all-allow acknowledgement.
    /// </summary>
    internal static Sdk.IsolationSessionProvisionOptions CreateProvisionOptions(string appId) =>
        new(
            new Sdk.StateAwareNetworkPolicy
            {
                Egress = new Sdk.NetworkEgressPolicy { Default = Sdk.NetworkAction.Allow },
                Ingress = new Sdk.NetworkIngressPolicy
                {
                    Default = Sdk.NetworkAction.Allow,
                    HostLoopback = Sdk.NetworkAction.Allow
                }
            })
        {
            AppId = appId
        };

    public Task StartAsync(
        MxcSandboxId sandboxId,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () =>
            {
                _lifecycle.StartSandbox(ToSdkId(sandboxId));
                return true;
            },
            cancellationToken);

    public async Task<MxcExecutionResult> ExecuteAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        CancellationToken cancellationToken)
    {
        string commandLine = RequireCommandLine(request);
        cancellationToken.ThrowIfCancellationRequested();
        Sdk.RunResult result = await TranslateAsync(
            _runtime,
            () => _lifecycle.ExecInSandboxAsync(
                ToSdkId(sandboxId),
                commandLine,
                options: null,
                cancellationToken)).ConfigureAwait(false);
        return new MxcExecutionResult(result.ExitCode, result.Stdout, result.Stderr);
    }

    /// <summary>
    /// Runs a command attached to this process's console, or relays its
    /// streams when the console is redirected.
    /// </summary>
    /// <remarks>
    /// The SDK refuses an attached execution unless standard input and output
    /// are both terminals, whereas redirected and piped OpenClaw invocations
    /// must keep working. Those run through the SDK's streaming execution
    /// instead, with this process relaying each stream.
    /// </remarks>
    public async Task<int> ExecuteAttachedAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        CancellationToken cancellationToken)
    {
        string commandLine = RequireCommandLine(request);
        cancellationToken.ThrowIfCancellationRequested();
        Sdk.SandboxId id = ToSdkId(sandboxId);
        return _stdio.InputAndOutputAreConsoles
            ? await ExecuteOnConsoleAsync(id, commandLine).ConfigureAwait(false)
            : await ExecuteRelayedAsync(id, commandLine, cancellationToken)
                .ConfigureAwait(false);
    }

    public Task StopAsync(
        MxcSandboxId sandboxId,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () =>
            {
                _lifecycle.StopSandbox(ToSdkId(sandboxId));
                return true;
            },
            cancellationToken);

    public Task DeprovisionAsync(
        MxcSandboxId sandboxId,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () =>
            {
                _lifecycle.DeprovisionSandbox(ToSdkId(sandboxId));
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Asks the SDK's host capability detector whether the IsolationSession
    /// backend is usable here. Discovery does not create a sandbox, so this is
    /// safe on the read-only setup path.
    /// </summary>
    public static Task<MxcBackendProbe> ProbeBackendAsync(
        MxcRuntimeLocation runtime,
        CancellationToken cancellationToken) =>
        ProbeBackendAsync(runtime, Sdk.MxcSandbox.GetAvailableBackends, cancellationToken);

    internal static async Task<MxcBackendProbe> ProbeBackendAsync(
        MxcRuntimeLocation runtime,
        Func<IReadOnlyList<Sdk.AvailableBackend>> discoverBackends,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(discoverBackends);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<Sdk.AvailableBackend> backends = await Task.Run(
            () => Translate(runtime, discoverBackends),
            CancellationToken.None).ConfigureAwait(false);
        Sdk.AvailableBackend? isolationSession = backends.FirstOrDefault(
            backend => backend.Backend == Sdk.ContainmentBackend.IsolationSession);
        return new MxcBackendProbe(
            isolationSession is not null,
            isolationSession?.Tier?.ToString(),
            isolationSession?.Warnings ?? []);
    }

    private async Task<int> ExecuteOnConsoleAsync(Sdk.SandboxId id, string commandLine)
    {
        using IDisposable capture = _console.Capture(_ => { });
        _console.InitializeUtf8();

        // The workload owns the console until it exits, and the SDK offers no
        // way to cancel the call, so it runs on a dedicated thread.
        Sdk.SandboxWaitResult outcome = await Task.Factory.StartNew(
            () => Translate(_runtime, () => _lifecycle.ExecInSandboxAttached(id, commandLine)),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);
        return outcome.ExitCode;
    }

    private async Task<int> ExecuteRelayedAsync(
        Sdk.SandboxId id,
        string commandLine,
        CancellationToken cancellationToken)
    {
        using Sdk.ISandboxProcess process = await InvokeAsync(
            () => _lifecycle.ExecInSandbox(id, commandLine),
            cancellationToken).ConfigureAwait(false);

        Task relayOutput = RelayOutputAsync(process.StandardOutput, _stdio.OpenOutput());
        Task relayError = RelayOutputAsync(process.StandardError, _stdio.OpenError());
        StartInputRelay(process.StandardInput);

        // Only this invocation's workload is killed. The session outlives the
        // command, and other OpenClaw invocations own their own workloads.
        Sdk.SandboxWaitResult outcome;
        using (cancellationToken.Register(() => TryKill(process)))
        {
            outcome = await TranslateAsync(
                _runtime,
                () => process.WaitAsync(CancellationToken.None)).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(relayOutput, relayError).ConfigureAwait(false);
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return outcome.ExitCode;
    }

    private void StartInputRelay(Stream? destination)
    {
        if (destination is null)
        {
            return;
        }

        Stream source = _stdio.OpenInput();

        // A console read blocks until the user types, possibly long after the
        // workload has exited, so input is relayed on a background thread that
        // is never awaited.
        _ = Task.Factory.StartNew(
            () => RelayInput(source, destination),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static void RelayInput(Stream source, Stream destination)
    {
        try
        {
            source.CopyTo(destination, RelayBufferSize);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or NotSupportedException)
        {
        }
        finally
        {
            // End of host input is end of workload input. IsolationSession
            // currently keeps its own stdin write handle open, so a workload
            // that reads to end of input does not observe it until that
            // platform defect is fixed.
            try
            {
                destination.Dispose();
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task RelayOutputAsync(Stream? source, Stream destination)
    {
        if (source is null)
        {
            return;
        }

        byte[] buffer = new byte[RelayBufferSize];
        bool deliver = true;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (!deliver)
            {
                continue;
            }

            try
            {
                await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                await destination.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The reader went away, as with `| Select-Object -First 1`.
                // Keep draining so the workload is not blocked on a full pipe.
                deliver = false;
            }
        }
    }

    private static void TryKill(Sdk.ISandboxProcess process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception exception) when (
            exception is Sdk.MxcException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Translate(_runtime, operation), CancellationToken.None);
    }

    private static T Translate<T>(MxcRuntimeLocation runtime, Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (MapFailure(runtime, exception) is { } mapped)
        {
            throw mapped;
        }
    }

    private static async Task<T> TranslateAsync<T>(
        MxcRuntimeLocation runtime,
        Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (MapFailure(runtime, exception) is { } mapped)
        {
            throw mapped;
        }
    }

    private static MxcException? MapFailure(MxcRuntimeLocation runtime, Exception exception) =>
        exception switch
        {
            Sdk.MxcException sdk => MxcException.FromSdk(sdk),
            ArgumentException argument => new MxcException(
                MxcErrorCode.MalformedRequest,
                $"MXC rejected the request this package built: {argument.Message}",
                innerException: argument),
            DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or TypeInitializationException => new MxcException(
                MxcErrorCode.RuntimeUnavailable,
                $"The MXC runtime at {runtime.NativeLibraryPath} could not be loaded: " +
                (exception.InnerException ?? exception).Message,
                innerException: exception),
            _ => null
        };

    private static string RequireCommandLine(MxcExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrWhiteSpace(request.CommandLine)
            ? throw new MxcException(
                MxcErrorCode.PolicyValidation,
                "An execution request requires a command line.")
            : request.CommandLine;
    }

    private static Sdk.SandboxId ToSdkId(MxcSandboxId sandboxId)
    {
        if (!sandboxId.IsIsolationSession)
        {
            throw new MxcException(
                MxcErrorCode.MalformedId,
                $"Sandbox id prefix '{sandboxId.BackendPrefix}' is not an " +
                "IsolationSession identity.");
        }

        return new Sdk.SandboxId(sandboxId.Value);
    }

    private static MxcProvisionResult ToProvisionResult(Sdk.ProvisionResult result)
    {
        if (string.IsNullOrWhiteSpace(result.SandboxId.Value))
        {
            throw new MxcException(
                MxcErrorCode.ProtocolViolation,
                "MXC provision result did not include a sandbox id.");
        }

        // Metadata is reported only when the backend supplies every field. A
        // partially populated workspace/agent identity is worse than none: it
        // would let later phases act on an unusable path or account.
        Sdk.IsolationSessionProvisionMetadata? metadata = result.IsolationSessionMetadata;
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
            MxcSandboxId.Parse(result.SandboxId.Value),
            provisionMetadata);
    }
}
