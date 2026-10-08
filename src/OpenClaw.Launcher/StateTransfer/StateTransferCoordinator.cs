using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Gateway;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.StateTransfer;

internal sealed partial class StateTransferCoordinator
{
    private readonly HostOptions _options;
    private readonly Func<SessionRuntime> _createRuntime;
    private readonly StateArchiveStore _store;
    private readonly Action<string> _log;
    private readonly Func<string, CancellationToken, Task<bool>>? _confirm;
    private readonly Func<SessionRuntime, CancellationToken, Task<GatewayStopResult>> _stopGateway;

    public StateTransferCoordinator(
        HostOptions options,
        Func<SessionRuntime> createRuntime,
        StateArchiveStore store,
        Action<string> log,
        Func<string, CancellationToken, Task<bool>>? confirm = null,
        Func<SessionRuntime, CancellationToken, Task<GatewayStopResult>>? stopGateway = null)
    {
        _options = options;
        _createRuntime = createRuntime;
        _store = store;
        _log = log;
        _confirm = confirm;
        _stopGateway = stopGateway ?? StopGatewayAsync;
    }

    public async Task<StateArchiveCommandResult> BackupAsync(
        BackupOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options.List)
            {
                return new StateArchiveCommandResult("backup", 0, Archives: _store.List());
            }
            string? destination = options.DryRun ? null : _store.ValidateDestination(options.Path);
            SessionRuntime runtime = _createRuntime();
            using ISessionLockHandle lifecycle = AcquireLock(runtime);
            _ = runtime.RequireSetup();
            SessionRecord record = await runtime.Coordinator
                .StartRecordedAsync(cancellationToken).ConfigureAwait(false);
            using var operation = new SessionWorkspaceOperation(record, runtime.IsCurrentSessionRecord);
            SessionStateTransferRequest request = CreateRequest(
                runtime,
                SessionStateTransferAction.Capture) with
            { DryRun = options.DryRun };
            string artifact = Path.Combine(
                operation.WorkspacePath, $"state-archive-{request.RequestId}.tar.gz");
            try
            {
                SessionStateTransferResult result = await runtime.Executor.TransferStateAsync(
                    record, runtime.RequireStagedHelper(record), operation, request, cancellationToken).ConfigureAwait(false);
                if (result.Archive is { } archive)
                {
                    using FileStream source = operation.OpenRead(archive.Path, protectContents: true);
                    string retained = await _store.PublishAsync(
                        source, archive, destination, protection: false, cancellationToken).ConfigureAwait(false);
                    result = result with { Archive = archive with { Path = retained } };
                }
                else if (!options.DryRun)
                {
                    throw new SessionException("The agent did not return a verified backup archive.");
                }
                return new StateArchiveCommandResult("backup", 0, result);
            }
            finally
            {
                operation.Delete(artifact);
            }
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            _log($"Backup failed: {DiagnosticFailure.Describe(exception)}");
            return new StateArchiveCommandResult("backup", 1, Error: exception.Message);
        }
    }

    private Task<GatewayStopResult> StopGatewayAsync(SessionRuntime runtime, CancellationToken cancellationToken) =>
        GatewayRuntime.Create(_options, runtime.Paths, runtime, _log).Controller
            .StopUnderLockAsync(runtime.HelperPath, cancellationToken);

    private SessionStateTransferRequest CreateRequest(
        SessionRuntime runtime,
        SessionStateTransferAction action)
    {
        string application = _options.PackagedApplicationDirectory
            ?? throw new SessionException("The immutable OpenClaw application was not found.");
        string nodeArchive = _options.PackagedNodeArchivePath
            ?? throw new SessionException("The packaged Node.js archive was not found.");
        string? nativeRoot = runtime.GetAgentNativeRoot();
        IReadOnlyDictionary<string, string> environment = OpenClawRuntimeEnvironment.Build();
        if (nativeRoot is not null)
        {
            environment = SessionExecutor.MergeEnvironment(
                environment,
                OpenClawRuntimeEnvironment.BuildNativeRedirect(application, nativeRoot));
        }
        return new SessionStateTransferRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Action = action,
            NodePath = runtime.RequireAgentNodePath(nodeArchive),
            ApplicationDirectory = application,
            NativeRootPath = nativeRoot,
            NativePreloadPath = nativeRoot is null ? null : Program.ResolveNativeRedirectPreloadPath(),
            Environment = environment
        };
    }

    private static ISessionLockHandle AcquireLock(SessionRuntime runtime) =>
        runtime.LifecycleLock.TryAcquire(TimeSpan.Zero)
        ?? throw new SessionException(
            "Another lifecycle operation is using this installation. Wait for it to finish, then retry.");

    private static bool IsOperationalFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SessionException or
            SessionLaunchException or Mxc.MxcException or System.Text.Json.JsonException;
}
