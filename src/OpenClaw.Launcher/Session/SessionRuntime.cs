using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Gateway;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// Assembles the session stack from the running installation.
/// </summary>
/// <remarks>
/// Composition lives here rather than in <c>Program</c> so both the agent and
/// control entry points build the same coordinator, over the same state root
/// and the same lifecycle lock, for the same package identity.
/// </remarks>
internal sealed class SessionRuntime
{
    /// <summary>
    /// Directory under the application base holding the guest helper, staged
    /// per architecture exactly like the MXC runtime.
    /// </summary>
    public const string HelperDirectoryName = "session-host";

    public const string HelperFileName = "openclaw-session-host.exe";

    private SessionRuntime(
        SessionCoordinator coordinator,
        SessionExecutor executor,
        IMxcSessionClient backend,
        string helperPath,
        string applicationId,
        HostPaths paths,
        SetupStateStore setupState,
        GatewayStateStore gatewayState,
        string lifecycleLockScope)
    {
        Coordinator = coordinator;
        Executor = executor;
        Backend = backend;
        HelperPath = helperPath;
        ApplicationId = applicationId;
        Paths = paths;
        SetupState = setupState;
        GatewayState = gatewayState;
        LifecycleLock = new NamedSessionLock(lifecycleLockScope);
    }

    public SessionCoordinator Coordinator { get; }

    public SessionExecutor Executor { get; }

    /// <summary>
    /// The one backend instance this process uses, so the gateway and ordinary
    /// invocations cannot end up talking to differently configured clients.
    /// </summary>
    public IMxcSessionClient Backend { get; }

    public string HelperPath { get; }

    public string ApplicationId { get; }

    public HostPaths Paths { get; }

    public SetupStateStore SetupState { get; }

    public GatewayStateStore GatewayState { get; }

    public ISessionLock LifecycleLock { get; }

    public ISessionLockHandle AcquireLifecycleLock() =>
        LifecycleLock.TryAcquire(SessionCoordinator.DefaultLockTimeout)
            ?? throw new SessionBusyException(SessionCoordinator.DefaultLockTimeout);

    public async Task<SessionRecord> StartForExecutionAsync(CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = AcquireLifecycleLock();
        RequireSetup();
        try
        {
            return await Coordinator.StartRecordedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MxcException exception) when (exception.Code == MxcErrorCode.RuntimeUnavailable)
        {
            throw new SessionCapabilityUnavailableException(exception.Message, exception);
        }
    }

    public string StageHelper(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return SessionHelperStager.Stage(
            HelperPath,
            RequireWorkspace(record));
    }

    public string RequireStagedHelper(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return SessionHelperStager.RequireStaged(
            HelperPath,
            RequireWorkspace(record));
    }

    public static SessionRuntime Create(Action<string> log) =>
        Create(
            HostPaths.Create(),
            MxcRuntimeLocator.Locate,
            AppContext.BaseDirectory,
            log);

    internal static SessionRuntime Create(
        HostPaths paths,
        Func<MxcRuntimeLocation> locateRuntime,
        string baseDirectory,
        Action<string> log,
        IMxcSessionClient? backend = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        if (paths.PackageFamilyName is null)
        {
            throw new SessionCapabilityUnavailableException(
                "OpenClaw is not running from its installed package, so it " +
                "has no identity to provision an isolated session with.");
        }

        string applicationId =
            PackageIdentity.ToApplicationId(paths.PackageFamilyName);
        IMxcSessionClient client = backend ?? new LazyMxcSessionClient(
            () => new MxcCliSessionClient(locateRuntime()));

        var coordinator = new SessionCoordinator(
            client,
            new SessionStateStore(paths.SessionStatePath),
            new NamedSessionLock(paths.SessionStatePath),
            applicationId,
            log);

        return new SessionRuntime(
            coordinator,
            new SessionExecutor(client, log, isCurrentRecord: IsCurrentSessionRecord),
            client,
            ResolveHelperPath(baseDirectory),
            applicationId,
            paths,
            new SetupStateStore(paths.SetupStatePath),
            new GatewayStateStore(paths.GatewayStatePath),
            paths.SessionStatePath + "_Installation");

        bool IsCurrentSessionRecord(SessionRecord record)
        {
            SessionStatus status = coordinator.GetRecordedStatus();
            return status.Record is not null &&
                string.Equals(status.Record.SandboxId, record.SandboxId, StringComparison.Ordinal) &&
                string.Equals(status.Record.Generation, record.Generation, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Requires both the explicit setup marker and the owned session record.
    /// </summary>
    /// <remarks>
    /// This is read-only. Starting the session remains a separate operation so
    /// a race with teardown cannot turn a stale marker into a new provision.
    /// </remarks>
    public SessionRecord RequireSetup()
    {
        SetupStateResult setup = SetupState.Read(ApplicationId);
        if (setup.Record is null)
        {
            throw new SessionException(
                setup.Fault == SetupStateFault.Missing
                    ? "OpenClaw has not been set up. Run `clawctl setup` first."
                    : setup.Detail!);
        }

        if (setup.Record.Phase != SetupPhase.Ready)
        {
            throw new SessionException(
                setup.Record.Phase == SetupPhase.TearingDown
                    ? "OpenClaw teardown is incomplete. Run `clawctl teardown` again."
                    : "OpenClaw setup is incomplete. Run `clawctl setup` again.");
        }

        SessionStatus session = Coordinator.GetRecordedStatus();
        if (session.Record is null)
        {
            throw new SessionException(
                session.Detail is null
                    ? "OpenClaw setup is incomplete because its isolated session is " +
                      "not recorded. Run `clawctl setup` again."
                    : $"{session.Detail} Run `clawctl setup` again.");
        }

        if (setup.Record.SandboxId is { } sandboxId &&
            !string.Equals(sandboxId, session.Record.SandboxId, StringComparison.Ordinal))
        {
            throw new SessionException(
                "The setup marker names a different session. Run `clawctl setup` again.");
        }

        return session.Record;
    }

    /// <summary>
    /// Validates local ownership before automatic host fallback.
    /// </summary>
    /// <remarks>
    /// Automatic fallback is allowed only when this installation has no saved
    /// session or setup state. A foreign, corrupt, or mismatched record is
    /// evidence that replacement or recovery is required, not permission to
    /// run the user's workload under a different profile. A session record
    /// without its setup marker must be completed with <c>clawctl setup</c>.
    /// </remarks>
    public void ValidateSavedOwnershipForHostFallback()
    {
        SessionStatus session = Coordinator.GetRecordedStatus();
        SetupStateResult setup = SetupState.Read(ApplicationId);
        if (session.Availability == SessionAvailability.None &&
            setup.Fault == SetupStateFault.Missing)
        {
            return;
        }

        if (session.Record is null)
        {
            throw new SessionException(
                $"{session.Detail ?? "The saved isolated-session record could not be used."} " +
                "Run `clawctl setup` to repair it.");
        }

        if (setup.Record is null)
        {
            throw new SessionException(
                "The isolated session is recorded but explicit setup has not completed. " +
                "Run `clawctl setup` before using automatic host fallback.");
        }

        if (setup.Record.Phase != SetupPhase.Ready)
        {
            throw new SessionException(
                "The saved isolated-session setup is incomplete. " +
                "Run `clawctl setup` before using automatic host fallback.");
        }

        if (!string.Equals(
                setup.Record.SandboxId,
                session.Record.SandboxId,
                StringComparison.Ordinal))
        {
            throw new SessionException(
                "The saved isolated-session records name different sessions. " +
                "Run `clawctl setup` to reconcile them.");
        }
    }

    /// <summary>
    /// Returns the Node.js executable extracted by the agent for the currently
    /// packaged runtime.
    /// </summary>
    public string RequireAgentNodePath(string packagedArchivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagedArchivePath);

        SetupStateResult setup = SetupState.Read(ApplicationId);
        SetupRecord? record = setup.Record;
        if (record?.AgentNodePath is not { Length: > 0 } executablePath ||
            !string.Equals(
                record.AgentNodeArchive,
                Path.GetFileName(packagedArchivePath),
                StringComparison.Ordinal))
        {
            throw new SessionException(
                "The isolated session runtime does not match this package. " +
                "Run `clawctl setup` again.");
        }

        return executablePath;
    }

    /// <summary>Records a completed guest runtime installation.</summary>
    public void CompleteSetup(
        SessionRecord session,
        SessionRuntimeInstallResult runtime,
        bool startupEnabled)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(runtime);

        SetupState.Write(new SetupRecord
        {
            ApplicationId = ApplicationId,
            SandboxId = session.SandboxId,
            Phase = SetupPhase.Ready,
            StartupEnabled = startupEnabled,
            CompletedUtc = DateTimeOffset.UtcNow,
            AgentNodePath = runtime.ExecutablePath,
            AgentNodeVersion = runtime.Version,
            AgentNodeArchive = runtime.ArchiveName
        });
    }

    internal static string ResolveHelperPath(string baseDirectory) =>
        Path.GetFullPath(
            Path.Combine(
                baseDirectory,
                HelperDirectoryName,
                MxcRuntimeLocator.CurrentArchitectureName(),
                HelperFileName));

    private static string RequireWorkspace(SessionRecord record) =>
        string.IsNullOrWhiteSpace(record.WorkspacePath)
            ? throw new SessionException(
                "The recorded session has no shared workspace, so the session " +
                "helper cannot be staged.")
            : record.WorkspacePath;
}
