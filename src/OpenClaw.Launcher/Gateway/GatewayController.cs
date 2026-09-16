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
    Starting,
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
    string Message);

internal sealed record GatewayStopResult(
    bool Stopped, string Message, string? Detail = null, bool Succeeded = true);

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
    private readonly Func<CancellationToken, Task<GatewayStartRequest>> _startRequestFactory;
    private readonly Func<SessionRecord> _requireSetup;
    private readonly ISessionLock _lifecycleLock;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;

    public GatewayController(
        SessionCoordinator sessions,
        ISessionGatewayClient client,
        GatewayStateStore store,
        Func<CancellationToken, Task<GatewayStartRequest>> startRequestFactory,
        Action<string> log,
        Func<SessionRecord> requireSetup,
        ISessionLock lifecycleLock,
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
        _requireSetup = requireSetup;
        _lifecycleLock = lifecycleLock;
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

        if (state.Record.LaunchPending)
        {
            return new GatewayStatusReport(GatewayState.Unknown, state.Record,
                "A previous gateway launch has not been confirmed.",
                "Do not start a replacement. Inspect diagnostics; `clawctl teardown` removes the owned session.");
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

        if (!string.Equals(session.Record.SandboxId, state.Record.SandboxId, StringComparison.Ordinal))
        {
            return new GatewayStatusReport(GatewayState.Unknown, state.Record,
                "The gateway record belongs to a different session.",
                "Run `clawctl teardown` to reconcile the owned installation.");
        }

        return Describe(
            state.Record,
            await InspectAsync(session.Record, state.Record, helperPath, cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>
    /// Starts the gateway only in a previously configured session.
    /// </summary>
    public async Task<GatewayStartResult> StartAsync(
        string helperPath,
        CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = AcquireLock();
        SessionRecord configured = _requireSetup();
        GatewayStateResult existing = _store.Read();
        if (existing.Record?.LaunchPending == true)
        {
            throw new SessionException(
                "A previous gateway launch was not confirmed. Inspect diagnostics before using `clawctl teardown` to remove the owned session.");
        }
        if (existing.Record is null && existing.Fault != GatewayStateFault.Missing)
        {
            throw new SessionException($"The gateway record could not be used: {existing.Detail}");
        }
        if (existing.Record is not null &&
            !string.Equals(existing.Record.SandboxId, configured.SandboxId, StringComparison.Ordinal))
        {
            throw new SessionException("The gateway record belongs to a different session.");
        }

        SessionRecord session = await _sessions
            .StartRecordedAsync(cancellationToken)
            .ConfigureAwait(false);

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

            if (inspection.ProcessFound && inspection.StartTimeMatches)
            {
                throw new SessionException(
                    "The owned gateway is alive but not serving. Inspect its log or run " +
                    "`clawctl gateway-service stop` before starting another.");
            }

            _log("The recorded gateway is no longer running; starting a new one.");
        }
        else if (existing.Fault != GatewayStateFault.Missing)
        {
            throw new SessionException(
                $"The gateway record could not be used: {existing.Detail}");
        }

        GatewayStartRequest request = await _startRequestFactory(cancellationToken).ConfigureAwait(false);
        _store.Write(new GatewayRecord
        {
            SandboxId = session.SandboxId,
            LaunchPending = true,
            Port = request.Port,
            ProcessStartTimeUtc = _clock.GetUtcNow(),
            StartedUtc = _clock.GetUtcNow()
        });
        GatewayStartOutcome started = await _client
            .StartAsync(session, request, cancellationToken)
            .ConfigureAwait(false);

        var record = new GatewayRecord
        {
            SandboxId = session.SandboxId,
            ProcessId = started.ProcessId,
            ProcessStartTimeUtc = started.ProcessStartTimeUtc,
            HelperPath = started.HelperPath,
            Port = request.Port,
            StatusPath = started.StatusPath,
            LogPath = started.LogPath,
            StartedUtc = _clock.GetUtcNow(),

            // An explicit earlier choice to disable logon recovery survives a
            // restart, so a later manual start does not quietly re-enable it.
            AutostartDisabled = existing.Record?.AutostartDisabled ?? false
        };

        _store.Write(record);

        SessionInspectResult observed = await InspectAsync(
            session, record, helperPath, cancellationToken).ConfigureAwait(false);
        GatewayState resultState = observed.IsOwnedAndHealthy ? GatewayState.Running
            : observed.Error is not null ? GatewayState.Unknown
            : observed.ProcessFound && observed.StartTimeMatches ? GatewayState.Starting
            : GatewayState.Stopped;

        if (observed.ListeningPorts is { Count: > 0 })
        {
            record = record with { ObservedPorts = observed.ListeningPorts };
            _store.Write(record);
        }

        return new GatewayStartResult(
            resultState,
            record,
            AlreadyRunning: false,
            resultState switch
            {
                GatewayState.Running => $"The gateway is running on {DescribePorts(record)}.",
                GatewayState.Starting => "The gateway process started but is not listening yet. Run `clawctl gateway-service status`.",
                GatewayState.Unknown => $"The gateway launch could not be verified: {observed.Error}",
                _ => DescribeExitedDuringStartup(record, observed)
            });
    }

    /// <summary>
    /// Names the port a user should actually connect to.
    /// </summary>
    /// <remarks>
    /// Prefers what was observed over what was requested, because OpenClaw
    /// resolves the port from its own configuration and the observed value is
    /// the only one that is true.
    /// </remarks>
    internal static string DescribePorts(GatewayRecord record)
    {
        if (record.ObservedPorts is { Count: > 0 } ports)
        {
            return ports.Count == 1
                ? $"port {ports[0]}"
                : $"ports {string.Join(", ", ports)}";
        }

        return record.Port is int pinned
            ? $"port {pinned}"
            : "its configured port";
    }

    /// <summary>
    /// Stops the gateway without removing the session or the user's data.
    /// </summary>
    public async Task<GatewayStopResult> StopAsync(
        string helperPath,
        CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = AcquireLock();
        return await StopUnderLockAsync(helperPath, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<GatewayStopResult> StopUnderLockAsync(
        string helperPath,
        CancellationToken cancellationToken)
    {
        GatewayStateResult state = _store.Read();
        if (state.Record?.LaunchPending == true)
        {
            return new GatewayStopResult(false,
                "The previous launch was not confirmed; its ownership intent was retained.",
                "Use `clawctl teardown` to stop/deprovision the owned session through MXC.", false);
        }
        if (state.Record is null)
        {
            return new GatewayStopResult(
                Stopped: false,
                "No gateway was recorded, so there was nothing to stop.",
                state.Fault == GatewayStateFault.Missing ? null : state.Detail,
                state.Fault == GatewayStateFault.Missing);
        }

        SessionStatus session = _sessions.GetRecordedStatus();
        if (session.Record is null)
        {
            return new GatewayStopResult(
                Stopped: false,
                "The gateway's session is not readable; its record was retained.",
                session.Detail, Succeeded: false);
        }

        if (!string.Equals(session.Record.SandboxId, state.Record.SandboxId, StringComparison.Ordinal))
        {
            return new GatewayStopResult(false,
                "The gateway record belongs to a different session.", Succeeded: false);
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
                inspection.Error, Succeeded: false);
        }

        if (!inspection.ProcessFound || !inspection.StartTimeMatches)
        {
            _store.Clear();
            return new GatewayStopResult(
                Stopped: false,
                "The recorded gateway was no longer running, so its record was " +
                "removed.");
        }

        SessionInspectResult stopped = await _client
            .StopAsync(session.Record, state.Record, helperPath, cancellationToken)
            .ConfigureAwait(false);

        if (stopped.Error is not null || (stopped.ProcessFound && stopped.StartTimeMatches))
        {
            return new GatewayStopResult(false,
                "The gateway stop could not be verified; its record was retained.",
                stopped.Error, Succeeded: false);
        }
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
        catch (Exception exception) when (
            exception is SessionException or Mxc.MxcException or IOException or UnauthorizedAccessException)
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
                record with { ObservedPorts = inspection.ListeningPorts },
                $"The gateway is running on " +
                $"{DescribePorts(record with { ObservedPorts = inspection.ListeningPorts })}.");
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
                    : DescribeStoppedGateway(record, inspection));
        }

        return new GatewayStatusReport(
            GatewayState.Unhealthy,
            record,
            "The gateway is running but is not serving.",
            DescribeUnhealthy(record, inspection));
    }

    private static string? DescribeStoppedGateway(
        GatewayRecord record,
        SessionInspectResult inspection)
    {
        if (string.IsNullOrWhiteSpace(inspection.SupervisorDetail))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(record.LogPath)
            ? $"The supervisor reported: {inspection.SupervisorDetail}"
            : $"The supervisor reported: {inspection.SupervisorDetail} " +
              $"Inspect the gateway log at '{record.LogPath}'.";
    }

    private static string DescribeExitedDuringStartup(
        GatewayRecord record,
        SessionInspectResult inspection) =>
        DescribeStoppedGateway(record, inspection) is string detail
            ? $"The gateway process exited during startup. {detail}"
            : "The gateway process exited during startup. Inspect its log.";

    /// <summary>
    /// Explains an unhealthy gateway in terms of what was observed.
    /// </summary>
    /// <remarks>
    /// A pinned port that nothing in our process tree listens on is its own
    /// diagnosis: OpenClaw's configuration moved the gateway somewhere else.
    /// </remarks>
    private static string DescribeUnhealthy(
        GatewayRecord record, SessionInspectResult inspection)
    {
        if (record.Port is int pinned &&
            inspection.ListeningPorts is { Count: > 0 } observed &&
            !observed.Contains(pinned))
        {
            return $"The gateway is listening on {string.Join(", ", observed)} " +
                $"rather than the configured port {pinned}. Check `gateway.port` " +
                "in the agent's OpenClaw configuration.";
        }

        if (record.Port is int configured)
        {
            return inspection.PortListening
                ? $"Port {configured} is in use by something that is not the gateway."
                : $"The gateway is not listening on port {configured}.";
        }

        return "The gateway is not listening on any port. Inspect its log.";
    }

    private ISessionLockHandle AcquireLock() =>
        _lifecycleLock.TryAcquire(SessionCoordinator.DefaultLockTimeout)
            ?? throw new SessionBusyException(SessionCoordinator.DefaultLockTimeout);
}
