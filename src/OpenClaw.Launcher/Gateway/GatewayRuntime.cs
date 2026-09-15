using System.Security.Principal;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>Assembles gateway management from the running installation.</summary>
internal sealed class GatewayRuntime
{
    private GatewayRuntime(GatewayController controller, string helperPath)
    {
        Controller = controller;
        HelperPath = helperPath;
    }

    public GatewayController Controller { get; }

    public string HelperPath { get; }

    public static GatewayPersistenceManager CreateRecoveryManager(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        HostPaths paths = HostPaths.Create();
        string packageFamilyName = paths.PackageFamilyName
            ?? throw new SessionException(
                "OpenClaw is not running from its installed package, so it cannot configure gateway recovery.");
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new SessionException(
                "The signed-in user's security identifier is unavailable, so gateway recovery cannot be configured.");

        return new GatewayPersistenceManager(
            new SchTasksGatewayScheduler(),
            new GatewayPersistenceOptions(
                userSid,
                packageFamilyName,
                paths.GatewayLauncherPath,
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                paths.StateRoot,
                "clawctl.exe",
                Path.Combine(Environment.SystemDirectory, "cmd.exe")),
            log);
    }

    public static GatewayRuntime Create(
        HostOptions options,
        Action<string> log,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        HostPaths paths = HostPaths.Create();
        if (paths.PackageFamilyName is null)
        {
            throw new SessionException(
                "OpenClaw is not running from its installed package, so it has no identity to manage a gateway with.");
        }

        SessionRuntime session = SessionRuntime.Create(log);
        var configuration = new GatewayConfigurationStore(paths.GatewayConfigurationPath);
        Func<CancellationToken, Task<NodeRuntime>> resolve = resolveNode
            ?? new Func<CancellationToken, Task<NodeRuntime>>(token => Task.FromResult(
                NodeRuntimeResolver.Resolve(
                    options.PackagedNodeArchivePath
                    ?? throw new SessionException(
                        "The packaged Node.js runtime archive was not found."))));

        async Task<GatewayStartRequest> CreateRequestAsync(CancellationToken cancellationToken)
        {
            string applicationDirectory = options.PackagedApplicationDirectory
                ?? throw new SessionException(
                    "The packaged OpenClaw application was not found, so the gateway cannot be started.");
            GatewayLaunchConfiguration launch = configuration.Resolve(
                paths.StateRoot,
                Environment.GetEnvironmentVariable);
            NodeRuntime packaged = await resolve(cancellationToken).ConfigureAwait(false);
            return new GatewayStartRequest(
                session.HelperPath,
                session.RequireAgentNodePath(packaged.Version),
                applicationDirectory,
                launch.WorkingDirectory!,
                launch.Port);
        }

        return new GatewayRuntime(
            new GatewayController(
                session.Coordinator,
                new SessionGatewayClient(session.Backend, log),
                new GatewayStateStore(paths.GatewayStatePath),
                CreateRequestAsync,
                log,
                session.RequireSetup,
                session.LifecycleLock),
            session.HelperPath);
    }
}
