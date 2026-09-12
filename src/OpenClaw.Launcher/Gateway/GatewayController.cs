using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Gateway;

/// <summary>What the gateway is actually doing, as opposed to what was recorded.</summary>
internal enum GatewayState
{
    /// <summary>No gateway has been started by this installation.</summary>
    NotStarted,

    /// <summary>A live, identity-matched gateway is serving.</summary>
    Running,

    /// <summary>
    /// A gateway was recorded but is no longer running. Stopping the session
    /// terminates detached work silently, so this is an ordinary outcome rather
    /// than a fault.
    /// </summary>
    Stopped,

    /// <summary>
    /// The recorded process is alive but is not serving, so it is neither
    /// usable nor safe to ignore.
    /// </summary>
    Unhealthy,

    /// <summary>
    /// Liveness could not be established. Reported as unknown rather than
    /// guessed: treating it as stopped would invite starting a second gateway
    /// beside a healthy one.
    /// </summary>
    Unknown,
}

internal sealed record GatewayStatusReport(
    GatewayState State,
    GatewayRecord? Record,
    string Message,
    string? Detail = null);

internal sealed record GatewayStartResult(
    GatewayState State,
    GatewayRecord Record,
    bool AlreadyRunning,
    GatewayPersistenceInstallResult? Persistence,
    string Message);

internal sealed record GatewayStopResult(bool Stopped, string Message, string? Detail = null);

/// <summary>
/// Owns this installation's background gateway.
/// </summary>
/// <remarks>
/// Nothing here infers liveness from the record. The record says what was
/// started; only an inspection inside the session says what is running.
/// </remarks>
internal sealed class GatewayController
{
    private readonly SessionCoordinator _sessions;
    private readonly ISessionGatewayClient _client;
    private readonly GatewayStateStore _store;
    private readonly GatewayPersistenceManager? _persistence;
    private readonly Func<GatewayStartRequest> _startRequestFactory;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;

    public GatewayController(
        SessionCoordinator sessions,
        ISessionGatewayClient client,
        GatewayStateStore store,
        Func<GatewayStartRequest> startRequestFactory,
        Action<string> log,
        GatewayPersistenceManager? persistence = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(startRequestFactory);
        ArgumentNullException.ThrowIfNull(log);

        _sessions = sessions;
        _client = client;
        _store = store;
        _startRequestFactory = startRequestFactory;
        _persistence = persistence;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Reports the gateway without starting one, provisioning a session, or
    /// writing anything.
    /// </summary>
    public async Task<GatewayStatusReport> GetStatusAsync(
        string helperPath,
        CancellationToken cancellationToken)
    {
        GatewayStateResult state = _store.Read();
        if (state.Record is null)
        {
            return state.Fault == GatewayStateFault.Missing
                ? new GatewayStatusReport(
                    GatewayState.NotStarted,
                    null,
                    "No gateway has been started.")
                : new GatewayStatusReport(
                    GatewayState.Unknown,
                    null,
                    "The gateway record could not be used.",
                    state.Detail);
        }

        SessionStatus session = _sessions.GetRecordedStatus();
        if (session.Record is null)
        {
            // The gateway cannot outlive the session it runs in, but saying so
            // is still a claim about state we have not observed.
            return new GatewayStatusReport(
                GatewayState.Unknown,
                state.Record,
                "The gateway's session is no longer recorded.",
                session.Detail);
        }

        return Describe(
            state.Record,
            await InspectAsync(session.Record, state.Record, helperPath, cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>
    /// Ensures a gateway is running, and on first start also configures logon
    /// recovery.
    /// </summary>
    public async Task<GatewayStartResult> StartAsync(
        string helperPath,
        CancellationToken cancellationToken)
    {
        SessionRecord session = await _sessions
            .EnsureStartedAsync(cancellationToken)
            .ConfigureAwait(false);

        GatewayStateResult existing = _store.Read();

        if (existing.Record is not null)
        {
            SessionInspectResult inspection = await InspectAsync(
                session,
                existing.Record,
                helperPath,
                cancellationToken).ConfigureAwait(false);

            if (inspection.IsOwnedAndHealthy)
            {
                _log("The gateway is already running.");
                return new GatewayStartResult(
                    GatewayState.Running,
                    existing.Record,
                    AlreadyRunning: true,
                    await EnsurePersistenceAsync(existing.Record, cancellationToken)
                        .ConfigureAwait(false),
                    "The gateway is already running.");
            }

            if (inspection.Error is not null)
            {
                // Starting a second gateway beside one we merely failed to
                // observe would leave two processes contending for one port.
                throw new SessionException(
                    "The gateway's state could not be established, so a new " +
                    $"one was not started: {inspection.Error}");
            }

            _log("The recorded gateway is no longer running; starting a new one.");
        }
        else if (existing.Fault != GatewayStateFault.Missing)
        {
            throw new SessionException(
                $"The gateway record could not be used: {existing.Detail}");
        }

        GatewayStartRequest request = _startRequestFactory();
        GatewayStartOutcome started = await _client
            .StartAsync(session, request, cancellationToken)
            .ConfigureAwait(false);

        var record = new GatewayRecord
        {
            SandboxId = session.SandboxId,
            ProcessId = started.ProcessId,
            ProcessStartTimeUtc = started.ProcessStartTimeUtc,
            Port = request.Port,
            StatusPath = started.StatusPath,
            LogPath = started.LogPath,
            StartedUtc = _clock.GetUtcNow(),

            // An explicit earlier choice to disable logon recovery survives a
            // restart, so a later manual start does not quietly re-enable it.
            AutostartDisabled = existing.Record?.AutostartDisabled ?? false
        };

        _store.Write(record);

        return new GatewayStartResult(
            GatewayState.Running,
            record,
            AlreadyRunning: false,
            await EnsurePersistenceAsync(record, cancellationToken)
                .ConfigureAwait(false),
            "The gateway is running.");
    }

    /// <summary>
    /// Stops the gateway without removing the session or the user's data.
    /// </summary>
    public async Task<GatewayStopResult> StopAsync(
        string helperPath,
        CancellationToken cancellationToken)
    {
        GatewayStateResult state = _store.Read();
        if (state.Record is null)
        {
            _store.Clear();
            return new GatewayStopResult(
                Stopped: false,
                "No gateway was recorded, so there was nothing to stop.",
                state.Fault == GatewayStateFault.Missing ? null : state.Detail);
        }

        SessionStatus session = _sessions.GetRecordedStatus();
        if (session.Record is null)
        {
            // The session is gone, so the gateway is too. Only the record is
            // left to clean up.
            _store.Clear();
            return new GatewayStopResult(
                Stopped: false,
                "The gateway's session is no longer recorded, so the stale " +
                "gateway record was removed.");
        }

        SessionInspectResult inspection = await InspectAsync(
            session.Record,
            state.Record,
            helperPath,
            cancellationToken).ConfigureAwait(false);

        if (inspection.Error is not null)
        {
            // Nothing is killed on a guess. Acting on an unverified identifier
            // could stop an unrelated process that inherited it.
            return new GatewayStopResult(
                Stopped: false,
                "The gateway could not be stopped because its state could not " +
                "be established.",
                inspection.Error);
        }

        if (!inspection.ProcessFound || !inspection.StartTimeMatches)
        {
            _store.Clear();
            return new GatewayStopResult(
                Stopped: false,
                "The recorded gateway was no longer running, so its record was " +
                "removed.");
        }

        await _client
            .StopAsync(session.Record, state.Record, helperPath, cancellationToken)
            .ConfigureAwait(false);

        _store.Clear();
        return new GatewayStopResult(Stopped: true, "The gateway is stopped.");
    }

    private async Task<SessionInspectResult> InspectAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client
                .InspectAsync(session, gateway, helperPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SessionException exception)
        {
            return new SessionInspectResult { Error = exception.Message };
        }
    }

    private static GatewayStatusReport Describe(
        GatewayRecord record,
        SessionInspectResult inspection)
    {
        if (inspection.Error is not null)
        {
            return new GatewayStatusReport(
                GatewayState.Unknown,
                record,
                "The gateway's state could not be established.",
                inspection.Error);
        }

        if (inspection.IsOwnedAndHealthy)
        {
            return new GatewayStatusReport(
                GatewayState.Running,
                record,
                $"The gateway is running on port {record.Port}.");
        }

        if (!inspection.ProcessFound || !inspection.StartTimeMatches)
        {
            return new GatewayStatusReport(
                GatewayState.Stopped,
                record,
                "The gateway is not running.",
                inspection.ProcessFound
                    ? "The recorded process identifier now belongs to an " +
                      "unrelated process."
                    : null);
        }

        return new GatewayStatusReport(
            GatewayState.Unhealthy,
            record,
            "The gateway is running but is not serving.",
            inspection.PortListening
                ? $"Port {record.Port} is in use by something that is not the gateway."
                : $"The gateway is not listening on port {record.Port}.");
    }

    private async Task<GatewayPersistenceInstallResult?> EnsurePersistenceAsync(
        GatewayRecord record,
        CancellationToken cancellationToken)
    {
        if (_persistence is null || record.AutostartDisabled)
        {
            return null;
        }

        return await _persistence.InstallAsync(cancellationToken).ConfigureAwait(false);
    }
}
