using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Mxc.Sdk.V1;
using SdkMxcException = Microsoft.Mxc.Sdk.V1.MxcException;

namespace OpenClaw.Launcher.Mxc;

internal interface IMxcSdkApi
{
    string NativeVersion { get; }
    PlatformSupport GetPlatformSupport();
    IReadOnlyList<AvailableBackend> GetAvailableBackends();
    ProvisionResult ProvisionContainer(ProvisionRequest request);
    LifecycleResult StartContainer(ContainerId id);
    IMxcProcess SpawnInContainer(ContainerId id, ExecutionRequest request);
    IMxcPtySession SpawnInContainerWithPty(
        ContainerId id,
        ExecutionRequest request,
        MxcPtySize size);
    LifecycleResult StopContainer(ContainerId id);
    LifecycleResult DeprovisionContainer(ContainerId id);
}

internal interface IMxcPtySession : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    IMxcStreamCloser? OutputCloser { get; }
    Task<WaitResult> WaitAsync(CancellationToken cancellationToken);
    bool TryGetExitCode(out int exitCode);
    void Kill();
    void Resize(MxcPtySize size);
}

internal sealed class MxcSdkSessionClient : IMxcSessionClient
{
    private readonly IMxcSdkApi _sdk;
    private readonly IMxcHostTerminal _terminal;

    public MxcSdkSessionClient(
        MxcRuntimeLocation runtime,
        IMxcSdkApi? sdk = null,
        IMxcHostTerminal? terminal = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (sdk is null)
        {
            MxcRuntimeLocator.InitializeSdkRuntime(runtime);
            _sdk = new MicrosoftMxcSdkApi();
        }
        else
        {
            _sdk = sdk;
        }

        _terminal = terminal ?? new WindowsMxcHostTerminal();
    }

    public async Task<MxcProvisionResult> ProvisionAsync(
        MxcProvisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                "A packaged caller must supply PFN:<packageFamilyName>.");
        }

        var provision = new IsolationSessionProvisionRequest(
            new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Allow },
                Ingress = new NetworkIngressPolicy
                {
                    Default = NetworkAction.Allow,
                    HostLoopback = NetworkAction.Allow
                }
            })
        {
            AppId = request.AppId
        };

        ProvisionResult result = await RunLifecycleAsync(
            () => _sdk.ProvisionContainer(provision),
            cancellationToken).ConfigureAwait(false);

        MxcProvisionMetadata? metadata = result.Metadata is IsolationSessionProvisionMetadata isolation &&
            !string.IsNullOrWhiteSpace(isolation.AgentUserName) &&
            !string.IsNullOrWhiteSpace(isolation.AgentUserSid) &&
            !string.IsNullOrWhiteSpace(isolation.EphemeralWorkspacePath)
                ? new MxcProvisionMetadata(
                    isolation.AgentUserName,
                    isolation.AgentUserSid,
                    isolation.EphemeralWorkspacePath)
                : null;

        return new MxcProvisionResult(
            MxcSandboxId.Parse(result.ContainerId.Value),
            metadata);
    }

    public async Task StartAsync(
        MxcSandboxId sandboxId,
        CancellationToken cancellationToken)
    {
        RequireIsolationSessionId(sandboxId);
        _ = await RunLifecycleAsync(
            () => _sdk.StartContainer(new ContainerId(sandboxId.Value)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<MxcExecutionResult> ExecuteAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        CancellationToken cancellationToken)
    {
        RequireIsolationSessionId(sandboxId);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CommandLine))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                "An execution request requires a command line.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        IMxcProcess process = await SpawnAsync(
            sandboxId,
            request.CommandLine,
            cancellationToken).ConfigureAwait(false);
        return await CaptureAsync(process, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExecuteAttachedAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        CancellationToken cancellationToken)
    {
        RequireIsolationSessionId(sandboxId);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CommandLine))
        {
            throw new MxcException(
                MxcErrorCode.PolicyValidation,
                "An execution request requires a command line.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_terminal.IsInteractive)
        {
            return await ExecuteAttachedPtyAsync(
                sandboxId,
                request.CommandLine,
                cancellationToken).ConfigureAwait(false);
        }

        IMxcProcess process = await SpawnAsync(
            sandboxId,
            request.CommandLine,
            cancellationToken).ConfigureAwait(false);
        return await RelayAttachedAsync(process, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(
        MxcSandboxId sandboxId,
        CancellationToken cancellationToken)
    {
        RequireIsolationSessionId(sandboxId);
        _ = await RunLifecycleAsync(
            () => _sdk.StopContainer(new ContainerId(sandboxId.Value)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeprovisionAsync(
        MxcSandboxId sandboxId,
        CancellationToken cancellationToken)
    {
        RequireIsolationSessionId(sandboxId);
        _ = await RunLifecycleAsync(
            () => _sdk.DeprovisionContainer(new ContainerId(sandboxId.Value)),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<MxcBackendProbe> ProbeBackendAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PlatformSupport support = _sdk.GetPlatformSupport();
                bool usable = support.AvailableMethods.Contains(
                    ContainmentBackend.IsolationSession);
                AvailableBackend? isolation = usable
                    ? _sdk.GetAvailableBackends().FirstOrDefault(
                        backend => backend.Backend == ContainmentBackend.IsolationSession)
                    : null;
                IReadOnlyList<string> warnings = isolation?.Warnings ?? [];
                if (!support.IsSupported && !string.IsNullOrWhiteSpace(support.Reason))
                {
                    warnings = [support.Reason, .. warnings];
                }

                return new MxcBackendProbe(
                    usable,
                    isolation?.Tier?.ToString(),
                    warnings);
            }
            catch (SdkMxcException exception)
            {
                throw MapException(exception);
            }
        }, cancellationToken);
    }

    [SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using disposable instances complete",
        Justification = "PTY input, output, and resize pumps are joined before process and stream disposal.")]
    private async Task<int> ExecuteAttachedPtyAsync(
        MxcSandboxId sandboxId,
        string commandLine,
        CancellationToken cancellationToken)
    {
        using IDisposable terminalMode = _terminal.EnterPtyMode();
        MxcPtySize initialSize = _terminal.GetSize();
        IMxcPtySession process = await SpawnPtyAsync(
            sandboxId,
            commandLine,
            initialSize,
            cancellationToken).ConfigureAwait(false);
        using (process)
        using (CancellationTokenSource inputLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (CancellationTokenSource resizeLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            CancellationTokenRegistration cancellationRegistration = default;
            PumpShutdown? shutdown = null;
            Stream? inputStream = null;
            IMxcStreamCloser? outputCloser = null;
            Task input = Task.CompletedTask;
            Task output = Task.CompletedTask;
            Task resize = Task.CompletedTask;
            Task inputMonitor = Task.CompletedTask;
            Task outputMonitor = Task.CompletedTask;
            Task resizeMonitor = Task.CompletedTask;
            try
            {
                inputStream = process.Input;
                Stream outputStream = process.Output;
                outputCloser = process.OutputCloser;
                Action stopAndCloseOutput = () =>
                {
                    CloseOutput(outputCloser);
                    TryKill(process);
                };
                shutdown = new PumpShutdown(stopAndCloseOutput);
                cancellationRegistration = cancellationToken.Register(shutdown.Cancel);
                input = _terminal.PumpInputAsync(inputStream, inputLifetime.Token);
                output = _terminal.PumpOutputAsync(
                    outputStream,
                    standardError: false,
                    CancellationToken.None);
                resize = _terminal.WatchResizeAsync(
                    size => TryResize(process, size),
                    resizeLifetime.Token);
                inputMonitor = StopOnPumpFailureAsync(input, shutdown.Fail, inputLifetime.Token);
                outputMonitor = StopOnPumpFailureAsync(output, shutdown.Fail, CancellationToken.None);
                resizeMonitor = StopOnPumpFailureAsync(resize, shutdown.Fail, resizeLifetime.Token);
                WaitResult result = await process.WaitAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                await inputLifetime.CancelAsync().ConfigureAwait(false);
                await resizeLifetime.CancelAsync().ConfigureAwait(false);
                CloseInput(inputStream);
                await AwaitPumpUnlessShutdownCanceledAsync(input, inputLifetime.Token)
                    .ConfigureAwait(false);
                await AwaitPumpUnlessShutdownCanceledAsync(resize, resizeLifetime.Token)
                    .ConfigureAwait(false);
                await output.ConfigureAwait(false);
                await Task.WhenAll(inputMonitor, outputMonitor, resizeMonitor)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return result.ExitCode;
            }
            catch (Exception)
            {
                bool canceled = shutdown?.WasCanceled ?? cancellationToken.IsCancellationRequested;
                await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
                await inputLifetime.CancelAsync().ConfigureAwait(false);
                await resizeLifetime.CancelAsync().ConfigureAwait(false);
                CloseInput(inputStream);
                if (shutdown is null)
                {
                    TryKill(process);
                    CloseOutput(outputCloser);
                }
                else
                {
                    shutdown.Stop();
                }
                await ObserveWaitAsync(process).ConfigureAwait(false);
                await ObservePumpsAsync(
                    [input, output, resize, inputMonitor, outputMonitor, resizeMonitor])
                    .ConfigureAwait(false);
                shutdown?.RethrowFailure();
                if (canceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw;
            }
            finally
            {
                await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
                outputCloser?.Dispose();
            }
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using disposable instances complete",
        Justification = "All stdio pumps are joined before process and stream disposal.")]
    private async Task<int> RelayAttachedAsync(
        IMxcProcess process,
        CancellationToken cancellationToken)
    {
        using (process)
        using (CancellationTokenSource inputLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            CancellationTokenRegistration cancellationRegistration = default;
            PumpShutdown? shutdown = null;
            Stream? input = null;
            IMxcStreamCloser? outputCloser = null;
            IMxcStreamCloser? errorCloser = null;
            Task inputPump = Task.CompletedTask;
            Task outputPump = Task.CompletedTask;
            Task errorPump = Task.CompletedTask;
            Task inputMonitor = Task.CompletedTask;
            Task outputMonitor = Task.CompletedTask;
            Task errorMonitor = Task.CompletedTask;
            try
            {
                input = process.StandardInput;
                Stream? output = process.StandardOutput;
                Stream? error = process.StandardError;
                outputCloser = process.StandardOutputCloser;
                errorCloser = process.StandardErrorCloser;
                Action stopAndCloseOutputs = () =>
                {
                    CloseOutput(outputCloser);
                    CloseOutput(errorCloser);
                    TryKill(process);
                };
                PumpShutdown pumpShutdown = new(stopAndCloseOutputs);
                shutdown = pumpShutdown;
                cancellationRegistration = cancellationToken.Register(pumpShutdown.Cancel);
                inputPump = input is null
                    ? Task.CompletedTask
                    : PumpAndCloseInputAsync(_terminal, input, inputLifetime.Token);
                outputPump = output is null
                    ? Task.CompletedTask
                    : _terminal.PumpOutputAsync(
                        output,
                        standardError: false,
                        CancellationToken.None);
                errorPump = error is null
                    ? Task.CompletedTask
                    : _terminal.PumpOutputAsync(
                        error,
                        standardError: true,
                        CancellationToken.None);
                inputMonitor = StopOnPumpFailureAsync(inputPump, pumpShutdown.Fail, inputLifetime.Token);
                outputMonitor = StopOnPumpFailureAsync(outputPump, pumpShutdown.Fail, CancellationToken.None);
                errorMonitor = StopOnPumpFailureAsync(errorPump, pumpShutdown.Fail, CancellationToken.None);
                WaitResult result = await process.WaitAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                await inputLifetime.CancelAsync().ConfigureAwait(false);
                CloseInput(input);
                await AwaitPumpUnlessShutdownCanceledAsync(inputPump, inputLifetime.Token)
                    .ConfigureAwait(false);
                await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);
                await Task.WhenAll(inputMonitor, outputMonitor, errorMonitor)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return result.ExitCode;
            }
            catch (Exception)
            {
                bool canceled = shutdown?.WasCanceled ?? cancellationToken.IsCancellationRequested;
                await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
                await inputLifetime.CancelAsync().ConfigureAwait(false);
                CloseInput(input);
                if (shutdown is null)
                {
                    TryKill(process);
                    CloseOutput(outputCloser);
                    CloseOutput(errorCloser);
                }
                else
                {
                    shutdown.Stop();
                }
                await ObserveWaitAsync(process).ConfigureAwait(false);
                await ObservePumpsAsync(
                    [inputPump, outputPump, errorPump, inputMonitor, outputMonitor, errorMonitor])
                    .ConfigureAwait(false);
                shutdown?.RethrowFailure();
                if (canceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw;
            }
            finally
            {
                await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
                outputCloser?.Dispose();
                errorCloser?.Dispose();
            }
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using disposable instances complete",
        Justification = "Both pumps are awaited or observed after kill and closer shutdown before streams are disposed.")]
    private static async Task<MxcExecutionResult> CaptureAsync(
        IMxcProcess process,
        CancellationToken cancellationToken)
    {
        using (process)
        {
            CancellationTokenRegistration cancellationRegistration = default;
            PumpShutdown? shutdown = null;
            Stream? output = null;
            Stream? error = null;
            IMxcStreamCloser? outputCloser = null;
            IMxcStreamCloser? errorCloser = null;
            Task stdoutPump = Task.CompletedTask;
            Task stderrPump = Task.CompletedTask;
            Task stdoutMonitor = Task.CompletedTask;
            Task stderrMonitor = Task.CompletedTask;
            try
            {
                output = process.StandardOutput;
                error = process.StandardError;
                outputCloser = process.StandardOutputCloser;
                errorCloser = process.StandardErrorCloser;
                Action stopAndCloseOutputs = () =>
                {
                    CloseOutput(outputCloser);
                    CloseOutput(errorCloser);
                    TryKill(process);
                };
                PumpShutdown pumpShutdown = new(stopAndCloseOutputs);
                shutdown = pumpShutdown;
                cancellationRegistration = cancellationToken.Register(pumpShutdown.Cancel);
                // Captured commands have no host input stream. Close the SDK-owned
                // stdin now so guests that read until EOF can finish.
                CloseInput(process.StandardInput);
                using var stdout = new MemoryStream();
                using var stderr = new MemoryStream();
                stdoutPump = output is null
                    ? Task.CompletedTask
                    : output.CopyToAsync(stdout, 81920, CancellationToken.None);
                stderrPump = error is null
                    ? Task.CompletedTask
                    : error.CopyToAsync(stderr, 81920, CancellationToken.None);
                stdoutMonitor = StopOnPumpFailureAsync(
                    stdoutPump,
                    pumpShutdown.Fail,
                    CancellationToken.None);
                stderrMonitor = StopOnPumpFailureAsync(
                    stderrPump,
                    pumpShutdown.Fail,
                    CancellationToken.None);
                try
                {
                    WaitResult result = await process.WaitAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
                    await Task.WhenAll(stdoutMonitor, stderrMonitor).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new MxcExecutionResult(
                        result.ExitCode,
                        Encoding.UTF8.GetString(stdout.ToArray()),
                        Encoding.UTF8.GetString(stderr.ToArray()));
                }
                catch (Exception)
                {
                    bool canceled = pumpShutdown.WasCanceled;
                    await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
                    pumpShutdown.Stop();
                    await ObserveWaitAsync(process).ConfigureAwait(false);
                    await ObservePumpsAsync(
                        [stdoutPump, stderrPump, stdoutMonitor, stderrMonitor])
                        .ConfigureAwait(false);
                    pumpShutdown.RethrowFailure();
                    if (canceled)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    throw;
                }
            }
            finally
            {
                await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
                outputCloser?.Dispose();
                errorCloser?.Dispose();
            }
        }
    }

    private async Task<IMxcProcess> SpawnAsync(
        MxcSandboxId sandboxId,
        string commandLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<IMxcProcess> spawn = Task.Run(() =>
        {
            try
            {
                return _sdk.SpawnInContainer(
                    new ContainerId(sandboxId.Value),
                    new ExecutionRequest(commandLine));
            }
            catch (SdkMxcException exception)
            {
                throw MapException(exception);
            }
        });

        IMxcProcess process = await spawn.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            await DisposeSpawnedProcessAsync(process).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return process;
    }

    private async Task<IMxcPtySession> SpawnPtyAsync(
        MxcSandboxId sandboxId,
        string commandLine,
        MxcPtySize size,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<IMxcPtySession> spawn = Task.Run(() =>
        {
            try
            {
                return _sdk.SpawnInContainerWithPty(
                    new ContainerId(sandboxId.Value),
                    new ExecutionRequest(commandLine),
                    size);
            }
            catch (SdkMxcException exception)
            {
                throw MapException(exception);
            }
        });

        IMxcPtySession process = await spawn.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            await DisposeSpawnedPtyAsync(process).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return process;
    }

    private static async Task<T> RunLifecycleAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<T> task = Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return operation();
            }
            catch (SdkMxcException exception)
            {
                throw MapException(exception);
            }
        });

        // The native call is synchronous and cannot be canceled safely. Once it
        // begins, wait for the result so a provisioned ID is not lost.
        return await task.ConfigureAwait(false);
    }

    private static async Task DisposeSpawnedProcessAsync(IMxcProcess process)
    {
        try
        {
            process.Kill();
        }
        catch (SdkMxcException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await process.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SdkMxcException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DisposeSpawnedPtyAsync(IMxcPtySession process)
    {
        try
        {
            process.Kill();
        }
        catch (SdkMxcException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await process.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SdkMxcException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void RequireIsolationSessionId(MxcSandboxId sandboxId)
    {
        if (!sandboxId.IsIsolationSession)
        {
            throw new MxcException(
                MxcErrorCode.MalformedId,
                $"Sandbox id prefix '{sandboxId.BackendPrefix}' is not an IsolationSession identity.");
        }
    }

    private static MxcException MapException(SdkMxcException exception)
    {
        string? backendCode = exception.Code.ToString();
        string message = exception.Message;
        if (!string.IsNullOrWhiteSpace(exception.Remediation))
        {
            message = $"{message} {exception.Remediation}";
        }

        if (!string.IsNullOrWhiteSpace(exception.NativeCode))
        {
            message = $"{message} (native code {exception.NativeCode})";
        }

        return new MxcException(
            exception.Code switch
            {
                ErrorCode.MalformedRequest => MxcErrorCode.MalformedRequest,
                ErrorCode.UnsupportedContainment or ErrorCode.UnsupportedPhase =>
                    MxcErrorCode.UnsupportedContainment,
                ErrorCode.BackendUnavailable => MxcErrorCode.RuntimeUnavailable,
                ErrorCode.MalformedId => MxcErrorCode.MalformedId,
                ErrorCode.StaleId => MxcErrorCode.StaleId,
                ErrorCode.PolicyValidation => MxcErrorCode.PolicyValidation,
                ErrorCode.BackendError => MxcErrorCode.BackendError,
                _ => MxcErrorCode.Unknown
            },
            message,
            backendCode,
            exception,
            exception.Operation);
    }

    private static void TryKill(IMxcProcess process)
    {
        try
        {
            if (!process.TryGetExitCode(out _))
            {
                process.Kill();
            }
        }
        catch (SdkMxcException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void TryKill(IMxcPtySession process)
    {
        try
        {
            if (!process.TryGetExitCode(out _))
            {
                process.Kill();
            }
        }
        catch (SdkMxcException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void TryResize(IMxcPtySession process, MxcPtySize size)
    {
        try
        {
            process.Resize(size);
        }
        catch (SdkMxcException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void CloseInput(Stream? input)
    {
        try
        {
            input?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task PumpAndCloseInputAsync(
        IMxcHostTerminal terminal,
        Stream destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await terminal.PumpInputAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // destination is the SDK-owned child stdin stream, not the host's
            // caller-owned Console.OpenStandardInput stream.
            CloseInput(destination);
        }
    }

    private static void CloseOutput(IMxcStreamCloser? closer)
    {
        try
        {
            closer?.Close();
        }
        catch (SdkMxcException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using disposable instances complete",
        Justification = "The waiter and all stream pumps are observed after shutdown before process disposal.")]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup observes and suppresses any pump fault so the original failure or cancellation is preserved.")]
    private static async Task ObservePumpsAsync(Task[] pumps)
    {
        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task AwaitPumpUnlessShutdownCanceledAsync(
        Task pump,
        CancellationToken shutdownToken)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
        }
    }

    private static async Task ObserveWaitAsync(IMxcProcess process)
    {
        try
        {
            await process.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SdkMxcException)
        {
        }
    }

    private static async Task ObserveWaitAsync(IMxcPtySession process)
    {
        try
        {
            await process.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SdkMxcException)
        {
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The original pump task is awaited by the caller after this monitor closes output readers and requests process shutdown.")]
    private static async Task StopOnPumpFailureAsync(
        Task pump,
        Action<Exception> stopPumps,
        CancellationToken shutdownToken)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            stopPumps(exception);
        }
    }

    private sealed class PumpShutdown(Action stopPumps)
    {
        private const int NoCause = 0;
        private const int Cancellation = 1;
        private const int PumpFailure = 2;
        private int _cause;
        private ExceptionDispatchInfo? _failure;

        public bool WasCanceled => Volatile.Read(ref _cause) == Cancellation;

        public void Stop() => stopPumps();

        public void Cancel()
        {
            Interlocked.CompareExchange(ref _cause, Cancellation, NoCause);
            stopPumps();
        }

        public void Fail(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _cause, PumpFailure, NoCause) == NoCause)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
            }
            stopPumps();
        }

        public void RethrowFailure() => Volatile.Read(ref _failure)?.Throw();
    }
}

internal sealed class MicrosoftMxcSdkApi : IMxcSdkApi
{
    public string NativeVersion => MxcPlatform.NativeVersion;

    public PlatformSupport GetPlatformSupport() => MxcPlatform.GetPlatformSupport();

    public IReadOnlyList<AvailableBackend> GetAvailableBackends() =>
        MxcPlatform.GetAvailableBackends();

    public ProvisionResult ProvisionContainer(ProvisionRequest request) =>
        MxcLifecycle.ProvisionContainer(request);

    public LifecycleResult StartContainer(ContainerId id) =>
        MxcLifecycle.StartContainer(id);

    public IMxcProcess SpawnInContainer(ContainerId id, ExecutionRequest request) =>
        MxcLifecycle.SpawnInContainer(id, request);

    public IMxcPtySession SpawnInContainerWithPty(
        ContainerId id,
        ExecutionRequest request,
        MxcPtySize size) =>
        new MicrosoftMxcPtySession(
            MxcLifecycle.SpawnInContainerWithPty(
                id,
                request,
                new SpawnInContainerWithPtyOptions { Size = size }));

    public LifecycleResult StopContainer(ContainerId id) => MxcLifecycle.StopContainer(id);

    public LifecycleResult DeprovisionContainer(ContainerId id) =>
        MxcLifecycle.DeprovisionContainer(id);
}

internal sealed class MicrosoftMxcPtySession(MxcPtyProcess process) : IMxcPtySession
{
    public Stream Input => process.Input;
    public Stream Output => process.Output;
    public IMxcStreamCloser? OutputCloser => process.StandardOutputCloser;
    public Task<WaitResult> WaitAsync(CancellationToken cancellationToken) =>
        process.WaitAsync(cancellationToken);
    public bool TryGetExitCode(out int exitCode) => process.TryGetExitCode(out exitCode);
    public void Kill() => process.Kill();
    public void Resize(MxcPtySize size) => process.Resize(size);
    public void Dispose() => process.Dispose();
}
