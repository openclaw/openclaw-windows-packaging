using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Session;

/// <summary>Runs the owned teardown in one lifecycle-lock acquisition.</summary>
internal sealed class TeardownOrchestrator
{
    private readonly ISessionLock _lock;
    private readonly GatewayPersistenceManager _recovery;
    private readonly GatewayController _gateway;
    private readonly SessionCoordinator _sessions;
    private readonly GatewayStateStore _gatewayState;
    private readonly GatewayConfigurationStore _configuration;
    private readonly SetupStateStore _setup;

    public TeardownOrchestrator(
        ISessionLock lifecycleLock,
        GatewayPersistenceManager recovery,
        GatewayController gateway,
        SessionCoordinator sessions,
        GatewayStateStore gatewayState,
        GatewayConfigurationStore configuration,
        SetupStateStore setup)
    {
        _lock = lifecycleLock;
        _recovery = recovery;
        _gateway = gateway;
        _sessions = sessions;
        _gatewayState = gatewayState;
        _configuration = configuration;
        _setup = setup;
    }

    public async Task<TeardownResult> RunAsync(string helperPath, CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = _lock.TryAcquire(SessionCoordinator.DefaultLockTimeout)
            ?? throw new SessionBusyException(SessionCoordinator.DefaultLockTimeout);
        return await RunUnderLockAsync(helperPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs teardown while the caller owns the installation lifecycle lock.</summary>
    public async Task<TeardownResult> RunUnderLockAsync(
        string helperPath,
        CancellationToken cancellationToken)
    {
        GatewayPersistenceRemovalResult recovery = await _recovery.UninstallAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!recovery.Succeeded)
        {
            return new TeardownResult(false, recovery.Message, recovery.Detail);
        }

        GatewayStopResult gateway = await _gateway.StopUnderLockAsync(
            helperPath, cancellationToken, clearRecord: false).ConfigureAwait(false);
        if (!gateway.Succeeded)
        {
            return new TeardownResult(false, gateway.Message, gateway.Detail);
        }

        SessionRemovalResult session = await _sessions.RemoveUnderLockAsync(cancellationToken)
            .ConfigureAwait(false);

        _gatewayState.Clear();
        _configuration.Clear();
        _setup.Clear();
        return new TeardownResult(
            true,
            session.Removed
                ? "OpenClaw isolated session and gateway service were removed."
                : "No isolated session was recorded; gateway service records were removed.",
            session.StopFailure);
    }
}

internal sealed record TeardownResult(bool Succeeded, string Message, string? Detail = null);
