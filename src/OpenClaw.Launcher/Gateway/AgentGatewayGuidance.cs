using System.ComponentModel;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Gateway;

internal sealed class AgentGatewayGuidance
{
    internal const string Hint =
        "Hint: The OpenClaw gateway is not running. " +
        "Run clawctl gateway-service start to start it.";

    /// <summary>
    /// Kept on its own line, and short enough not to wrap, so the command
    /// stays selectable and greppable in a narrow terminal.
    /// </summary>
    internal const string RetryGuidance =
        "Retry with clawctl gateway-service start.";
    internal static readonly TimeSpan AdvisoryTimeout = TimeSpan.FromSeconds(5);

    private readonly ISessionLock _lifecycleLock;
    private readonly Func<CancellationToken, Task<SessionConfigReadinessResult>> _checkReadiness;
    private readonly Func<CancellationToken, Task<GatewayStatusReport>> _getGatewayStatus;
    private readonly GatewayGuidanceStateStore _state;
    private readonly Func<string> _getLogonSessionId;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;
    private readonly Action<TextWriter> _writeHint;
    private readonly Func<
        TextWriter,
        Func<IProgress<GatewayStartProgress>, Task<GatewayStartResult>>,
        Task<GatewayStartResult>> _narrateGatewayStart;
    private readonly Func<
        IProgress<GatewayStartProgress>,
        Action<GatewayStartResult>,
        Task<GatewayStartResult>> _startGatewayUnderLock;
    private readonly Action<TextWriter, string> _writeGatewayStartFailure;
    private readonly Func<string, string?> _readEnvironmentVariable;

    public AgentGatewayGuidance(
        ISessionLock lifecycleLock,
        Func<CancellationToken, Task<SessionConfigReadinessResult>> checkReadiness,
        Func<CancellationToken, Task<GatewayStatusReport>> getGatewayStatus,
        GatewayGuidanceStateStore state,
        Func<string> getLogonSessionId,
        Action<string> log,
        TimeProvider? clock = null,
        Action<TextWriter>? writeHint = null,
        Func<
            TextWriter,
            Func<IProgress<GatewayStartProgress>, Task<GatewayStartResult>>,
            Task<GatewayStartResult>>? narrateGatewayStart = null,
        Func<
            IProgress<GatewayStartProgress>,
            Action<GatewayStartResult>,
            Task<GatewayStartResult>>? startGateway = null,
        Action<TextWriter, string>? writeGatewayStartFailure = null,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        _lifecycleLock = lifecycleLock;
        _checkReadiness = checkReadiness;
        _getGatewayStatus = getGatewayStatus;
        _state = state;
        _getLogonSessionId = getLogonSessionId;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _writeHint = writeHint ?? (writer => writer.WriteLine(Hint));
        _narrateGatewayStart = narrateGatewayStart ??
            ((TextWriter _, Func<IProgress<GatewayStartProgress>, Task<GatewayStartResult>> start) =>
                start(new Progress<GatewayStartProgress>()));
        _startGatewayUnderLock = startGateway ?? ((_, _) => throw new InvalidOperationException(
            "Gateway start is not configured."));
        _writeGatewayStartFailure = writeGatewayStartFailure ??
            ((writer, message) =>
            {
                writer.WriteLine(message);
                writer.WriteLine(RetryGuidance);
            });
        _readEnvironmentVariable = readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public async Task EvaluateAsync(
        int openClawExitCode,
        bool interactive,
        TextWriter error)
    {
        try
        {
            string logonSessionId = _getLogonSessionId();
            if (_state.IsAcknowledged(logonSessionId))
            {
                _log("Gateway guidance is already acknowledged for this Windows logon.");
                return;
            }

            using var readinessTimeout = new CancellationTokenSource(AdvisoryTimeout);
            SessionConfigReadinessResult readiness =
                await _checkReadiness(readinessTimeout.Token).ConfigureAwait(false);
            _log($"Agent config readiness is {readiness.State} ({readiness.Reason}).");
            if (readiness.State is not SessionConfigReadinessState.StartupEligible)
            {
                return;
            }

            using var statusTimeout = new CancellationTokenSource(AdvisoryTimeout);
            GatewayStatusReport status =
                await _getGatewayStatus(statusTimeout.Token).ConfigureAwait(false);
            _log($"Post-OpenClaw gateway state is {status.State}.");
            if (status.State == GatewayState.Running)
            {
                Acknowledge(
                    logonSessionId,
                    GatewayGuidanceAcknowledgement.GatewayObservedRunning);
                return;
            }

            switch (AutoBehaviorPolicy.DecideGatewayAction(
                openClawExitCode,
                interactive,
                readiness.State.GetValueOrDefault(),
                status.State,
                _readEnvironmentVariable))
            {
                case GatewayAutoAction.Hint:
                    WriteHintIfUnacknowledged(logonSessionId, error);
                    break;
                case GatewayAutoAction.Start:
                    await StartGatewayAsync(logonSessionId, error).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception exception) when (
            exception is MxcException or SessionException or SessionLaunchException or
            OperationCanceledException or
            IOException or UnauthorizedAccessException or InvalidOperationException or
            ObjectDisposedException or Win32Exception)
        {
            _log($"Gateway guidance was skipped: {exception.GetType().Name}: {exception.Message}");
        }
    }

    public void AcknowledgeManualStart()
    {
        try
        {
            Acknowledge(
                _getLogonSessionId(),
                GatewayGuidanceAcknowledgement.ManualStartInvoked);
        }
        catch (Exception exception) when (
            exception is SessionException or
            IOException or UnauthorizedAccessException or
            InvalidOperationException or Win32Exception)
        {
            _log(
                $"Gateway guidance acknowledgement failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void WriteHintIfUnacknowledged(
        string logonSessionId,
        TextWriter error)
    {
        using ISessionLockHandle? handle =
            _lifecycleLock.TryAcquire(AdvisoryTimeout);
        if (handle is null)
        {
            _log("Gateway guidance hint was skipped because lifecycle state is busy.");
            return;
        }

        if (_state.IsAcknowledged(logonSessionId))
        {
            _log("Gateway guidance was acknowledged while postflight checks were running.");
            return;
        }

        _writeHint(error);
    }

    private void Acknowledge(
        string logonSessionId,
        GatewayGuidanceAcknowledgement acknowledgement)
    {
        using ISessionLockHandle handle =
            _lifecycleLock.TryAcquire(AdvisoryTimeout)
            ?? throw new SessionBusyException(AdvisoryTimeout);
        _state.Write(logonSessionId, acknowledgement, _clock.GetUtcNow());
    }

    private async Task StartGatewayAsync(string logonSessionId, TextWriter error)
    {
        try
        {
            using ISessionLockHandle handle =
                _lifecycleLock.TryAcquire(AdvisoryTimeout)
                ?? throw new SessionBusyException(AdvisoryTimeout);
            if (_state.IsAcknowledged(logonSessionId))
            {
                _log("Gateway guidance was acknowledged while postflight checks were running.");
                return;
            }

            GatewayStartResult result = await _narrateGatewayStart(
                error,
                progress => _startGatewayUnderLock(
                    progress,
                    _ => AcknowledgeUnderLock(
                        logonSessionId,
                        GatewayGuidanceAcknowledgement.GatewayObservedRunning)))
                .ConfigureAwait(false);
            if (result.State != GatewayState.Running && !result.AlreadyRunning)
            {
                WriteGatewayStartFailure(
                    error,
                    $"Gateway start finished in {result.State} state: {result.Message}");
            }
        }
        catch (Exception exception) when (
            exception is MxcException or SessionException or SessionLaunchException or
            OperationCanceledException or
            IOException or UnauthorizedAccessException or InvalidOperationException or
            ObjectDisposedException or Win32Exception)
        {
            WriteGatewayStartFailure(
                error,
                $"Gateway start failed: {exception.Message}");
            _log($"Gateway auto-start failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void WriteGatewayStartFailure(TextWriter error, string detail) =>
        _writeGatewayStartFailure(error, detail);

    private void AcknowledgeUnderLock(
        string logonSessionId,
        GatewayGuidanceAcknowledgement acknowledgement)
    {
        try
        {
            _state.Write(logonSessionId, acknowledgement, _clock.GetUtcNow());
        }
        catch (Exception exception) when (
            exception is SessionException or
            IOException or UnauthorizedAccessException or
            InvalidOperationException or Win32Exception)
        {
            _log(
                $"Gateway guidance acknowledgement failed after start: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}
