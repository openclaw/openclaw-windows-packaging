using System.Text;
using Sdk = Microsoft.Mxc.Sdk.V1;

namespace OpenClaw.Launcher.Mxc;

internal sealed record MxcTerminalProcess(
    Sdk.IMxcProcess Process,
    Action<Sdk.MxcPtySize> Resize);

internal interface IMxcSdkLifecycle
{
    Sdk.ProvisionResult Provision(Sdk.IsolationSessionProvisionRequest request);
    Sdk.LifecycleResult Start(Sdk.ContainerId id);
    Sdk.IMxcProcess Spawn(Sdk.ContainerId id, Sdk.ExecutionRequest request);
    MxcTerminalProcess SpawnTerminal(Sdk.ContainerId id, Sdk.ExecutionRequest request, Sdk.MxcPtySize size);
    Sdk.LifecycleResult Stop(Sdk.ContainerId id);
    Sdk.LifecycleResult Deprovision(Sdk.ContainerId id);
}

internal sealed class MxcSdkLifecycle : IMxcSdkLifecycle
{
    public Sdk.ProvisionResult Provision(Sdk.IsolationSessionProvisionRequest request) =>
        Sdk.MxcLifecycle.ProvisionContainer(request);

    public Sdk.LifecycleResult Start(Sdk.ContainerId id) => Sdk.MxcLifecycle.StartContainer(id);

    public Sdk.IMxcProcess Spawn(Sdk.ContainerId id, Sdk.ExecutionRequest request) =>
        Sdk.MxcLifecycle.SpawnInContainer(id, request);

    public MxcTerminalProcess SpawnTerminal(
        Sdk.ContainerId id, Sdk.ExecutionRequest request, Sdk.MxcPtySize size)
    {
        Sdk.MxcPtyProcess process = Sdk.MxcLifecycle.SpawnInContainerWithPty(
            id, request, new Sdk.SpawnInContainerWithPtyOptions { Size = size });
        return new MxcTerminalProcess(process, process.Resize);
    }

    public Sdk.LifecycleResult Stop(Sdk.ContainerId id) => Sdk.MxcLifecycle.StopContainer(id);

    public Sdk.LifecycleResult Deprovision(Sdk.ContainerId id) => Sdk.MxcLifecycle.DeprovisionContainer(id);
}

internal interface IMxcHostStdio
{
    bool IsTerminal { get; }
    Sdk.MxcPtySize TerminalSize { get; }
    void PrepareTerminal();
    Stream OpenOutput();
    Stream OpenError();
    Task CopyInputAsync(Stream destination, CancellationToken cancellationToken);
    Task WatchSizeAsync(Action<Sdk.MxcPtySize> resize, CancellationToken cancellationToken);
}

internal sealed class MxcSdkSessionClient : IMxcSessionClient
{
    private readonly MxcRuntimeLocation _runtime;
    private readonly IMxcSdkLifecycle _lifecycle;
    private readonly IHostConsole _console;
    private readonly IMxcHostStdio _stdio;
    private readonly Action<string> _log;

    public MxcSdkSessionClient(
        MxcRuntimeLocation runtime,
        IMxcSdkLifecycle? lifecycle = null,
        IHostConsole? console = null,
        IMxcHostStdio? stdio = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _lifecycle = lifecycle ?? new MxcSdkLifecycle();
        _console = console ?? WindowsHostConsole.Instance;
        _stdio = stdio ?? WindowsMxcHostStdio.Instance;
        _log = log ?? (_ => { });
    }

    internal static Sdk.IsolationSessionProvisionRequest CreateProvisionRequest(string appId) =>
        new(new Sdk.NetworkPolicy
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

    public async Task<MxcProvisionResult> ProvisionAsync(
        MxcProvisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                "A packaged caller must supply PFN:<packageFamilyName>.");
        }

        Sdk.ProvisionResult result = await InvokeAsync(
            () => _lifecycle.Provision(CreateProvisionRequest(request.AppId)),
            cancellationToken).ConfigureAwait(false);
        ReportWarnings(result.Warnings);
        return new MxcProvisionResult(
            MxcSandboxId.Parse(result.ContainerId.Value),
            result.Metadata is Sdk.IsolationSessionProvisionMetadata metadata
                ? new MxcProvisionMetadata(
                    metadata.AgentUserName, metadata.AgentUserSid, metadata.EphemeralWorkspacePath)
                : null);
    }

    public async Task StartAsync(MxcSandboxId sandboxId, CancellationToken cancellationToken) =>
        ReportWarnings((await InvokeAsync(
            () => _lifecycle.Start(ToSdkId(sandboxId)), cancellationToken).ConfigureAwait(false)).Warnings);

    public async Task StopAsync(MxcSandboxId sandboxId, CancellationToken cancellationToken) =>
        ReportWarnings((await InvokeAsync(
            () => _lifecycle.Stop(ToSdkId(sandboxId)), cancellationToken).ConfigureAwait(false)).Warnings);

    public async Task DeprovisionAsync(MxcSandboxId sandboxId, CancellationToken cancellationToken) =>
        ReportWarnings((await InvokeAsync(
            () => _lifecycle.Deprovision(ToSdkId(sandboxId)), cancellationToken).ConfigureAwait(false)).Warnings);

    public Task<MxcExecutionResult> ExecuteAsync(
        MxcSandboxId sandboxId, MxcExecutionRequest request, CancellationToken cancellationToken) =>
        TranslateAsync(() => ExecuteCoreAsync(sandboxId, request, cancellationToken));

    private async Task<MxcExecutionResult> ExecuteCoreAsync(
        MxcSandboxId sandboxId, MxcExecutionRequest request, CancellationToken cancellationToken)
    {
        Sdk.ExecutionRequest execution = RequireExecution(request);
        using Sdk.IMxcProcess process = await InvokeAsync(
            () => _lifecycle.Spawn(ToSdkId(sandboxId), execution),
            cancellationToken).ConfigureAwait(false);
        request.ProcessCreated?.Invoke();
        if (process.StandardInput is { } input)
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
        using CancellationTokenRegistration cancellation = RegisterCancellation(process, request, cancellationToken);
        using Sdk.IMxcStreamCloser? outputCloser = RequireReaderCloser(process, request, stdout: true);
        using Sdk.IMxcStreamCloser? errorCloser = RequireReaderCloser(process, request, stdout: false);
        Task<byte[]> output = CaptureOutputAsync(process.StandardOutput);
        Task<byte[]> error = CaptureOutputAsync(process.StandardError);
        Sdk.WaitResult result;
        try
        {
            result = await TranslateAsync(() => process.WaitAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                NotifyForegroundExited(request, outputCloser, errorCloser);
            }
            finally
            {
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
        }
        ReportWarnings(process.Warnings);
        cancellationToken.ThrowIfCancellationRequested();
        return new MxcExecutionResult(
            result.ExitCode, Encoding.UTF8.GetString(await output.ConfigureAwait(false)),
            Encoding.UTF8.GetString(await error.ConfigureAwait(false)));
    }

    private async Task<byte[]> CaptureOutputAsync(Stream? source)
    {
        using var output = new MemoryStream();
        await RelayOutputAsync(source, output).ConfigureAwait(false);
        return output.ToArray();
    }

    private static Sdk.IMxcStreamCloser? RequireReaderCloser(
        Sdk.IMxcProcess process, MxcExecutionRequest request, bool stdout)
    {
        Stream? source = stdout ? process.StandardOutput : process.StandardError;
        if (source is null || request.CancelInvocation is null)
        {
            return null;
        }
        return (stdout ? process.StandardOutputCloser : process.StandardErrorCloser)
            ?? throw new MxcException(MxcErrorCode.BackendError,
                "MXC did not provide the output reader cancellation required for invocation-owned cleanup.");
    }

    private static void NotifyForegroundExited(
        MxcExecutionRequest request, Sdk.IMxcStreamCloser? outputCloser, Sdk.IMxcStreamCloser? errorCloser)
    {
        try
        {
            request.ForegroundExited?.Invoke();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
            InvalidOperationException or TimeoutException or IOException or MxcException or Sdk.MxcException or
            Session.SessionException)
        {
            // A failed drain must remain a failure, but its pipe joins must not
            // wait forever for an unconfirmed writer to disappear.
            try
            {
                outputCloser?.Close();
            }
            finally
            {
                errorCloser?.Close();
            }
            throw;
        }
    }

    public Task<int> ExecuteAttachedAsync(
        MxcSandboxId sandboxId, MxcExecutionRequest request, CancellationToken cancellationToken) =>
        TranslateAsync(() => ExecuteAttachedCoreAsync(sandboxId, request, cancellationToken));

    private async Task<int> ExecuteAttachedCoreAsync(
        MxcSandboxId sandboxId, MxcExecutionRequest request, CancellationToken cancellationToken)
    {
        Sdk.ExecutionRequest execution = RequireExecution(request);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable capture = _console.Capture(_log);
        _console.InitializeUtf8();
        bool terminal = _stdio.IsTerminal;
        if (terminal)
        {
            _stdio.PrepareTerminal();
        }

        MxcTerminalProcess? terminalProcess = terminal
            ? await InvokeAsync(
                () => _lifecycle.SpawnTerminal(ToSdkId(sandboxId), execution, _stdio.TerminalSize),
                cancellationToken).ConfigureAwait(false)
            : null;
        using Sdk.IMxcProcess process = terminalProcess?.Process ?? await InvokeAsync(
            () => _lifecycle.Spawn(ToSdkId(sandboxId), execution),
            cancellationToken).ConfigureAwait(false);
        request.ProcessCreated?.Invoke();
        using Stream output = _stdio.OpenOutput();
        using Stream error = _stdio.OpenError();
        return await RelayForegroundAsync(
            process, terminalProcess, output, error, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RelayForegroundAsync(
        Sdk.IMxcProcess process,
        MxcTerminalProcess? terminalProcess,
        Stream output,
        Stream error,
        MxcExecutionRequest request,
        CancellationToken cancellationToken)
    {
        bool terminal = terminalProcess is not null;
        using var relays = new CancellationTokenSource();
        Task stdout = Task.CompletedTask;
        Task stderr = Task.CompletedTask;
        Task input = Task.CompletedTask;
        Task resize = Task.CompletedTask;
        Task<Sdk.WaitResult>? wait = null;
        bool foregroundNotified = false;
        using CancellationTokenRegistration cancellation = RegisterCancellation(process, request, cancellationToken);
        using Sdk.IMxcStreamCloser? outputCloser = RequireReaderCloser(process, request, stdout: true);
        using Sdk.IMxcStreamCloser? errorCloser = terminal ? null :
            RequireReaderCloser(process, request, stdout: false);

        void NotifyOnce()
        {
            if (!foregroundNotified)
            {
                foregroundNotified = true;
                NotifyForegroundExited(request, outputCloser, errorCloser);
            }
        }

        try
        {
            stdout = RelayOutputAsync(process.StandardOutput, output);
            stderr = terminal ? Task.CompletedTask : RelayOutputAsync(process.StandardError, error);
            input = RelayInputAsync(process, relays.Token);
            resize = terminalProcess is null ? Task.CompletedTask :
                _stdio.WatchSizeAsync(terminalProcess.Resize, relays.Token);
            wait = TranslateAsync(() => process.WaitAsync(CancellationToken.None));
            // Relay failures must interrupt the workload, not disappear while
            // an unrelated native wait keeps the command running.
            var pending = new List<Task> { wait, stdout, stderr, input, resize };
            while (!wait.IsCompleted)
            {
                Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            Sdk.WaitResult result = await wait.ConfigureAwait(false);
            NotifyOnce();
            await relays.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr, input, resize).ConfigureAwait(false);
            ReportWarnings(process.Warnings);
            cancellationToken.ThrowIfCancellationRequested();
            return result.ExitCode;
        }
        catch (Exception exception) when (MapFailure(_runtime, exception) is { } mapped)
        {
            throw mapped;
        }
        finally
        {
            if (wait is null || !wait.IsCompletedSuccessfully)
            {
                if (request.CancelInvocation is { } cancel)
                {
                    cancel();
                }
                else
                {
                    Kill(process);
                }
            }
            wait ??= TranslateAsync(() => process.WaitAsync(CancellationToken.None));
            await ObserveCleanupAsync(wait).ConfigureAwait(false);
            try
            {
                NotifyOnce();
            }
            finally
            {
                await relays.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(stdout, stderr, input, resize).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or OperationCanceledException or ObjectDisposedException or
                        Sdk.MxcException or MxcException or System.ComponentModel.Win32Exception)
                {
                    if (exception is not OperationCanceledException)
                    {
                        _log($"MXC execution cleanup failed: {DiagnosticFailure.Describe(exception)}");
                    }
                }
            }
        }
    }

    internal static Task<MxcBackendProbe> ProbeBackendAsync(
        MxcRuntimeLocation runtime, CancellationToken cancellationToken) =>
        ProbeBackendAsync(
            runtime, Sdk.MxcPlatform.GetAvailableBackends,
            () => Sdk.MxcPlatform.NativeVersion, cancellationToken);

    internal static async Task<MxcBackendProbe> ProbeBackendAsync(
        MxcRuntimeLocation runtime,
        Func<IReadOnlyList<Sdk.AvailableBackend>> discover,
        Func<string> readNativeVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Translate(runtime, () =>
        {
            Sdk.AvailableBackend? backend = discover().FirstOrDefault(
                item => item.Backend == Sdk.ContainmentBackend.IsolationSession);
            return new MxcBackendProbe(
                backend is not null, backend?.Tier?.ToString(),
                backend?.Warnings ?? [], readNativeVersion());
        }), CancellationToken.None).ConfigureAwait(false);
    }

    private Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Dispatched native calls cannot be interrupted. Await spawn/provision
        // so their newly created handle/identity is never lost to cancellation.
        return Task.Run(() => Translate(_runtime, () =>
        {
            return operation();
        }), CancellationToken.None);
    }

    private Task<T> TranslateAsync<T>(Func<Task<T>> operation) =>
        TranslateAsync(_runtime, operation);

    private async Task RelayInputAsync(Sdk.IMxcProcess process, CancellationToken cancellationToken)
    {
        if (process.StandardInput is { } destination)
        {
            await _stdio.CopyInputAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<T> TranslateAsync<T>(
        MxcRuntimeLocation runtime, Func<Task<T>> operation)
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
                    $"The {runtime.Provenance?.Package ?? "Microsoft.Mxc.Sdk"} runtime could not be loaded: " +
                    (exception.InnerException ?? exception).Message,
                    innerException: exception),
            _ => null
        };

    private static Sdk.ContainerId ToSdkId(MxcSandboxId sandboxId) =>
        sandboxId.IsIsolationSession ? new Sdk.ContainerId(sandboxId.Value) :
            throw new MxcException(MxcErrorCode.MalformedId,
                $"Sandbox id prefix '{sandboxId.BackendPrefix}' is not an IsolationSession identity.");

    private static Sdk.ExecutionRequest RequireExecution(MxcExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrWhiteSpace(request.CommandLine)
            ? throw new MxcException(MxcErrorCode.PolicyValidation,
                "An execution request requires a command line.")
            : new Sdk.ExecutionRequest(request.CommandLine);
    }

    private void ReportWarnings(IEnumerable<string> warnings)
    {
        foreach (string warning in warnings)
        {
            _log($"MXC warning: {Gateway.DiagnosticsRedactor.Redact(DiagnosticFailure.SingleLine(warning))}");
        }
    }

    private CancellationTokenRegistration RegisterCancellation(
        Sdk.IMxcProcess process, MxcExecutionRequest request, CancellationToken cancellationToken) =>
        cancellationToken.Register(request.CancelInvocation ?? (() => Kill(process)));

    private void Kill(Sdk.IMxcProcess process)
    {
        try
        {
            process.Kill();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Sdk.MxcException exception)
        {
            _log($"MXC execution termination failed: {DiagnosticFailure.Describe(MxcException.FromSdk(exception))}");
        }
    }

    private async Task ObserveCleanupAsync(params Task[] tasks)
    {
        foreach (Task task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or OperationCanceledException or
                    ObjectDisposedException or Sdk.MxcException or MxcException)
            {
                if (exception is not OperationCanceledException)
                {
                    _log($"MXC execution cleanup failed: {DiagnosticFailure.Describe(exception)}");
                }
            }
        }
    }

    private async Task RelayOutputAsync(Stream? source, Stream destination)
    {
        if (source is null)
        {
            return;
        }

        byte[] buffer = new byte[81920];
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
            catch (IOException exception) when (
                (exception.HResult & 0xffff) is 109 or 232 or 233)
            {
                deliver = false;
                _log("The downstream output pipe closed; draining the MXC execution's remaining output.");
            }
        }
    }
}
