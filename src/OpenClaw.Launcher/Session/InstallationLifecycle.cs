using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// Composes the package-owned dependencies used while installing or resetting
/// the isolated OpenClaw session.
/// </summary>
internal interface IInstallationLifecycle
{
    SessionRuntime CreateRuntime(Action<string> log);

    Task EnsureSessionSupportedAsync(
        CancellationToken cancellationToken);

    PackageRuntimeMetadata ValidatePackageRuntime(HostOptions options, SessionRuntime runtime);

    ISessionLockHandle AcquireLifecycleLock(SessionRuntime runtime);

    Task<TeardownResult> TeardownAsync(
        HostOptions options,
        SessionRuntime runtime,
        Action<string> log,
        bool lockAlreadyHeld,
        CancellationToken cancellationToken);

    IInstallationStateCleaner CreateStateCleaner(SessionRuntime runtime);

    Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
        Action<string> log,
        CancellationToken cancellationToken);

    Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
        Action<string> log,
        CancellationToken cancellationToken);
}

internal sealed class InstallationLifecycle : IInstallationLifecycle
{
    public static InstallationLifecycle Production { get; } = new();

    public SessionRuntime CreateRuntime(Action<string> log) => SessionRuntime.Create(log);

    public async Task EnsureSessionSupportedAsync(
        CancellationToken cancellationToken)
    {
        MxcReadinessReport readiness = await MxcReadiness.ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionSupportPolicy.EnsureSupported(
            HostPaths.Create().PackageFamilyName,
            readiness);
    }

    public PackageRuntimeMetadata ValidatePackageRuntime(HostOptions options, SessionRuntime runtime)
    {
        string nodeArchive = options.PackagedNodeArchivePath
            ?? throw new FileNotFoundException("The packaged Node.js runtime archive was not found.");
        Version version = NodeRuntimeInstaller.GetArchiveVersion(
            nodeArchive,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        if (!File.Exists(runtime.HelperPath))
        {
            throw new FileNotFoundException(
                "The packaged session helper was not found.",
                runtime.HelperPath);
        }

        return new PackageRuntimeMetadata(nodeArchive, version, runtime.HelperPath);
    }

    public ISessionLockHandle AcquireLifecycleLock(SessionRuntime runtime) =>
        runtime.AcquireLifecycleLock();

    public Task<TeardownResult> TeardownAsync(
        HostOptions options,
        SessionRuntime runtime,
        Action<string> log,
        bool lockAlreadyHeld,
        CancellationToken cancellationToken) =>
        RunTeardownAsync(options, runtime, log, lockAlreadyHeld, cancellationToken);

    private static Task<TeardownResult> RunTeardownAsync(
        HostOptions options,
        SessionRuntime runtime,
        Action<string> log,
        bool lockAlreadyHeld,
        CancellationToken cancellationToken)
    {
        TeardownOrchestrator teardown =
            GatewayRuntime.CreateTeardownOrchestrator(options, runtime, log);
        return lockAlreadyHeld
            ? teardown.RunUnderLockAsync(runtime.HelperPath, force: true, cancellationToken)
            : teardown.RunAsync(runtime.HelperPath, force: true, cancellationToken);
    }

    public IInstallationStateCleaner CreateStateCleaner(SessionRuntime runtime) =>
        new InstallationStateCleaner(
            [runtime.Paths.StateRoot, HostDataPaths.GetProductLocalStateRoot()]);

    public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
        Action<string> log,
        CancellationToken cancellationToken) =>
        GatewayRuntime.CreateRecoveryManager(log).InstallAsync(cancellationToken);

    public Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
        Action<string> log,
        CancellationToken cancellationToken) =>
        GatewayRuntime.CreateRecoveryManager(log).GetStatusAsync(cancellationToken);
}

internal sealed record PackageRuntimeMetadata(
    string NodeArchivePath,
    Version NodeVersion,
    string HelperPath);

internal interface IInstallationStateCleaner
{
    void Clear();
}
