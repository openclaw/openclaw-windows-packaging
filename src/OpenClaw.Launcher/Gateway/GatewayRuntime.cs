using System.Security.Principal;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>Assembles gateway management from the running installation.</summary>
internal sealed partial class GatewayRuntime
{
    private readonly HostPaths _paths;
    private readonly SessionRuntime _session;

    private GatewayRuntime(
        GatewayController controller,
        string helperPath,
        HostPaths paths,
        SessionRuntime session)
    {
        Controller = controller;
        HelperPath = helperPath;
        _paths = paths;
        _session = session;
    }

    public GatewayController Controller { get; }

    public string HelperPath { get; }

    private SessionRuntime Session => _session;

    private static bool FileExists(string path) => File.Exists(path);

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
        Action<string> log)
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
        return Create(options, paths, session, log);
    }

    internal static GatewayRuntime Create(
        HostOptions options,
        HostPaths paths,
        SessionRuntime session,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);

        return new GatewayRuntime(
            CreateController(options, paths, session, log),
            session.HelperPath,
            paths,
            session);
    }

    internal static TeardownOrchestrator CreateTeardownOrchestrator(
        HostOptions options,
        SessionRuntime session,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);

        HostPaths paths = HostPaths.Create();
        return new TeardownOrchestrator(
            session.LifecycleLock,
            CreateRecoveryManager(log),
            CreateController(options, paths, session, log),
            session.Coordinator,
            session.GatewayState,
            new GatewayConfigurationStore(paths.GatewayConfigurationPath),
            session.SetupState);
    }

    private static GatewayController CreateController(
        HostOptions options,
        HostPaths paths,
        SessionRuntime session,
        Action<string> log)
    {
        var configuration = new GatewayConfigurationStore(paths.GatewayConfigurationPath);
        async Task<GatewayStartRequest> CreateRequestAsync(CancellationToken cancellationToken)
        {
            string applicationDirectory = options.PackagedApplicationDirectory
                ?? throw new SessionException(
                    "The packaged OpenClaw application was not found, so the gateway cannot be started.");
            GatewayLaunchConfiguration launch = configuration.Resolve(
                Environment.GetEnvironmentVariable);
            string archivePath = options.PackagedNodeArchivePath
                ?? throw new SessionException(
                    "The packaged Node.js runtime archive was not found.");
            Version packagedVersion = NodeRuntimeInstaller.GetArchiveVersion(
                archivePath,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            return new GatewayStartRequest(
                session.HelperPath,
                session.RequireAgentNodePath(packagedVersion),
                applicationDirectory,
                launch.Port);
        }

        return new GatewayController(
            session.Coordinator,
            new SessionGatewayClient(session.Backend, log),
            session.GatewayState,
            CreateRequestAsync,
            log,
            session.RequireSetup,
            session.LifecycleLock);
    }
}
