using System.Diagnostics.CodeAnalysis;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// A session lifecycle operation could not proceed.
/// </summary>
internal class SessionException : Exception
{
    public SessionException()
    {
    }

    public SessionException(string message)
        : base(message)
    {
    }

    public SessionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The recorded session exists but cannot be used, and replacing it silently
/// would risk abandoning a live backend session.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification =
        "The fault is what callers act on: it distinguishes a missing record " +
        "from one that is merely unreadable, which decides whether replacing " +
        "the session is safe. A message-only constructor would allow that " +
        "distinction to be lost.")]
internal sealed class SessionStateException : SessionException
{
    public SessionStateException(SessionStateFault fault, string detail)
        : base(detail) => Fault = fault;

    public SessionStateFault Fault { get; }
}

/// <summary>
/// Another process held the lifecycle lock for longer than the caller allowed.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification =
        "The timeout is the message: this exception exists to tell the user " +
        "how long another process was given before the wait was abandoned. A " +
        "message-only form would let that number be omitted or contradicted.")]
internal sealed class SessionBusyException : SessionException
{
    public SessionBusyException(TimeSpan timeout)
        : base(
            "Another OpenClaw process is changing the session and did not " +
            $"finish within {timeout.TotalSeconds:0.#} seconds.")
    {
    }
}

/// <summary>
/// Whether a usable session is recorded, without consulting the backend.
/// </summary>
internal enum SessionAvailability
{
    /// <summary>No session has been recorded; a first one may be created.</summary>
    None,

    /// <summary>A usable record exists. It says nothing about liveness.</summary>
    Recorded,

    /// <summary>A record exists but cannot be used. Recovery is required.</summary>
    Unusable,
}

/// <summary>
/// What the local record says. Liveness is deliberately not claimed here: the
/// backend offers no authoritative session enumeration, so recorded identity
/// and live evidence must stay separate.
/// </summary>
internal sealed record SessionStatus(
    SessionAvailability Availability,
    SessionRecord? Record,
    SessionStateFault? Fault,
    string? Detail);

/// <summary>
/// The outcome of removing the owned session.
/// </summary>
/// <param name="Removed">False when there was nothing to remove.</param>
/// <param name="StopFailure">
/// Set when stop failed but deprovision was still attempted, so the caller can
/// report the degraded path instead of claiming a clean teardown.
/// </param>
internal sealed record SessionRemovalResult(bool Removed, string? StopFailure);

/// <summary>
/// The result of ensuring that an owned session is started.
/// </summary>
/// <param name="Record">The started session record.</param>
/// <param name="SupersededRecord">
/// The previously owned record MXC explicitly reported as stale, when setup
/// had to replace it.
/// </param>
internal sealed record SessionStartResult(
    SessionRecord Record,
    SessionRecord? SupersededRecord);

/// <summary>
/// Owns this installation's isolated session across processes.
/// </summary>
internal sealed class SessionCoordinator
{
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

    private readonly IMxcSessionClient _backend;
    private readonly SessionStateStore _store;
    private readonly ISessionLock _lifecycleLock;
    private readonly string _applicationId;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lockTimeout;

    public SessionCoordinator(
        IMxcSessionClient backend,
        SessionStateStore store,
        ISessionLock lifecycleLock,
        string applicationId,
        Action<string> log,
        TimeProvider? clock = null,
        TimeSpan? lockTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lifecycleLock);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentNullException.ThrowIfNull(log);

        _backend = backend;
        _store = store;
        _lifecycleLock = lifecycleLock;
        _applicationId = applicationId;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
    }

    /// <summary>
    /// Reports the recorded session without provisioning, starting, or writing.
    /// </summary>
    public SessionStatus GetRecordedStatus()
    {
        SessionStateResult result = _store.Read(_applicationId);
        if (result.Record is not null)
        {
            return new SessionStatus(
                SessionAvailability.Recorded,
                result.Record,
                null,
                null);
        }

        SessionAvailability availability = result.Fault == SessionStateFault.Missing
            ? SessionAvailability.None
            : SessionAvailability.Unusable;

        return new SessionStatus(availability, null, result.Fault, result.Detail);
    }

    /// <summary>
    /// Returns the owned, started session, creating it only on first use.
    /// </summary>
    public async Task<SessionRecord> EnsureStartedAsync(
        CancellationToken cancellationToken) =>
        (await EnsureStartedWithResultAsync(cancellationToken).ConfigureAwait(false)).Record;

    /// <summary>
    /// Returns the owned, started session and identifies an explicitly stale
    /// record when setup replaced it.
    /// </summary>
    internal async Task<SessionStartResult> EnsureStartedWithResultAsync(
        CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = AcquireLock();

        SessionStateResult state = _store.Read(_applicationId);
        if (state.Record is not null)
        {
            _log("Reusing the recorded OpenClaw session.");
            try
            {
                await StartAsync(state.Record, cancellationToken)
                    .ConfigureAwait(false);
                return new SessionStartResult(state.Record, null);
            }
            catch (MxcException exception) when (exception.Code == MxcErrorCode.StaleId)
            {
                _log(
                    "The recorded OpenClaw provision no longer exists. " +
                    "Provisioning a replacement for this installation.");
                SessionRecord replacement = await ProvisionAndStartAsync(cancellationToken)
                    .ConfigureAwait(false);
                return new SessionStartResult(replacement, state.Record);
            }
        }

        if (state.Fault != SessionStateFault.Missing)
        {
            // Provisioning over a record we merely failed to read would abandon
            // a live backend session and the user's guest profile with it.
            throw new SessionStateException(state.Fault!.Value, state.Detail!);
        }

        _log("Creating the first OpenClaw session for this installation.");
        SessionRecord provisioned = await ProvisionAndStartAsync(cancellationToken)
            .ConfigureAwait(false);
        return new SessionStartResult(provisioned, null);
    }

    private async Task<SessionRecord> ProvisionAndStartAsync(
        CancellationToken cancellationToken)
    {
        MxcProvisionResult provisioned = await _backend
            .ProvisionAsync(new MxcProvisionRequest(_applicationId), cancellationToken)
            .ConfigureAwait(false);

        // Ownership is recorded before the session is started. A crash between
        // provision and start would otherwise leave a sandbox nothing claims,
        // and the backend cannot be asked which sandboxes are ours.
        var record = new SessionRecord
        {
            SandboxId = provisioned.SandboxId.Value,
            ApplicationId = _applicationId,
            AgentUserName = provisioned.Metadata?.AgentUserName,
            AgentUserSid = provisioned.Metadata?.AgentUserSid,
            WorkspacePath = provisioned.Metadata?.EphemeralWorkspacePath,
            CreatedUtc = _clock.GetUtcNow(),
        };
        try
        {
            _store.Write(record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"The new session could not be recorded: {exception.Message}. Deprovisioning this attempt.");
            try
            {
                await _backend.DeprovisionAsync(provisioned.SandboxId, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (MxcException cleanup)
            {
                throw new SessionException(
                    $"The session record could not be saved, and deprovision failed: {cleanup.Message}. " +
                    $"Recover the owned sandbox ID from diagnostics before retrying. ID: {provisioned.SandboxId.Value}",
                    exception);
            }
            throw;
        }

        await StartAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Starts the recorded session without provisioning a replacement.
    /// </summary>
    /// <remarks>
    /// OpenClaw execution and gateway recovery use this path after explicit
    /// setup. A missing record is a setup error, never permission to create a
    /// new session implicitly.
    /// </remarks>
    public async Task<SessionRecord> StartRecordedAsync(
        CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = AcquireLock();

        SessionRecord record = RequireUsableRecordOrNull()
            ?? throw new SessionException(
                "No isolated session is recorded. Run `clawctl setup` first.");

        await StartAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Stops the session, keeping the provision and the guest profile.
    /// </summary>
    /// <returns>False when no session is recorded.</returns>
    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = AcquireLock();

        SessionRecord? record = RequireUsableRecordOrNull();
        if (record is null)
        {
            return false;
        }

        await _backend
            .StopAsync(record.ToSandboxIdOrThrow(), null, cancellationToken)
            .ConfigureAwait(false);
        _log("Stopped the OpenClaw session; its profile and data are retained.");
        return true;
    }

    /// <summary>
    /// Stops and deprovisions the session, then forgets it.
    /// </summary>
    /// <remarks>
    /// This destroys the guest profile and workspace. The local record is
    /// cleared only after deprovision succeeds, so a failed teardown leaves the
    /// session owned rather than orphaned beyond recovery.
    /// </remarks>
    public async Task<SessionRemovalResult> RemoveAsync(
        CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = AcquireLock();

        SessionRecord? record = RequireUsableRecordOrNull();
        if (record is null)
        {
            return new SessionRemovalResult(false, null);
        }

        MxcSandboxId sandboxId = record.ToSandboxIdOrThrow();

        string? stopFailure = null;
        try
        {
            await _backend.StopAsync(sandboxId, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MxcException exception)
        {
            // Deprovision is what actually releases the account, profile, and
            // workspace. Abandoning removal because stop failed would leave
            // more behind than continuing does, so the failure is reported
            // rather than used to stop the teardown.
            stopFailure = exception.Message;
            _log($"Stop failed before removal; continuing to deprovision: {exception.Message}");
        }

        await _backend.DeprovisionAsync(sandboxId, null, cancellationToken)
            .ConfigureAwait(false);
        _store.Clear();
        _log("Removed the OpenClaw session and its guest profile.");
        return new SessionRemovalResult(true, stopFailure);
    }

    /// <summary>
    /// Discards the local record without contacting the backend.
    /// </summary>
    /// <remarks>
    /// This is the deliberate escape from a record that cannot be read well
    /// enough to deprovision. It abandons whatever the backend still holds, so
    /// it is never reached implicitly by another operation.
    /// </remarks>
    public void ForgetRecordedState()
    {
        using ISessionLockHandle handle = AcquireLock();
        _store.Clear();
        _log("Discarded the local session record without contacting the backend.");
    }

    private async Task StartAsync(
        SessionRecord record,
        CancellationToken cancellationToken)
    {
        // Start is issued on every path, including reuse. A stopped session
        // fails execution with backend_error rather than restarting itself, and
        // the backend offers no way to ask whether a session is running, so the
        // only way to guarantee a usable session is to start it.
        await _backend
            .StartAsync(record.ToSandboxIdOrThrow(), null, cancellationToken)
            .ConfigureAwait(false);
    }

    private SessionRecord? RequireUsableRecordOrNull()
    {
        SessionStateResult state = _store.Read(_applicationId);
        if (state.Record is not null)
        {
            return state.Record;
        }

        if (state.Fault == SessionStateFault.Missing)
        {
            return null;
        }

        throw new SessionStateException(state.Fault!.Value, state.Detail!);
    }

    private ISessionLockHandle AcquireLock() =>
        _lifecycleLock.TryAcquire(_lockTimeout)
        ?? throw new SessionBusyException(_lockTimeout);
}

internal static class SessionRecordExtensions
{
    public static MxcSandboxId ToSandboxIdOrThrow(this SessionRecord record) =>
        MxcSandboxId.Parse(record.SandboxId);
}
