using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests;

// Any invocation that reaches this lifecycle has started real installation
// work, which help, version, and rejected input must never do.
internal sealed class FailIfWorkStartsLifecycle : IInstallationLifecycle
{
    private static InvalidOperationException Fail() =>
        new("Setup ran for an invocation that should not have started it.");

    public SessionRuntime CreateRuntime(Action<string> log) => throw Fail();

    public Task EnsureSessionSupportedAsync(CancellationToken cancellationToken) =>
        throw Fail();

    public PackageRuntimeMetadata ValidatePackageRuntime(
        HostOptions options,
        SessionRuntime sessionRuntime) => throw Fail();

    public ISessionLockHandle AcquireLifecycleLock(SessionRuntime sessionRuntime) =>
        throw Fail();

    public Task<TeardownResult> TeardownAsync(
        HostOptions options,
        SessionRuntime sessionRuntime,
        Action<string> log,
        bool lockAlreadyHeld,
        CancellationToken cancellationToken) => throw Fail();

    public IInstallationStateCleaner CreateStateCleaner(SessionRuntime sessionRuntime) =>
        throw Fail();

    public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
        Action<string> log,
        CancellationToken cancellationToken) => throw Fail();
}
