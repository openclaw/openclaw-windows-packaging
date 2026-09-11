using System.Security.Principal;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Assembles the gateway stack from the running installation.
/// </summary>
/// <remarks>
/// Composition lives here rather than in <c>Program</c> so the control
/// commands, the logon task's start verb, and the persistence manager all agree
/// on one state root, one package identity, and one launcher file.
/// </remarks>
public sealed class GatewayRuntime
{
    private GatewayRuntime(
        GatewayController controller,
        GatewayPersistenceManager persistence,
        string helperPath)
    {
        Controller = controller;
        Persistence = persistence;
        HelperPath = helperPath;
    }

    public GatewayController Controller { get; }

    public GatewayPersistenceManager Persistence { get; }

    public string HelperPath { get; }

    public static GatewayRuntime Create(HostOptions options, Action<string> log) =>
        Create(
            options,
            HostPaths.Create(),
            SessionRuntime.Create(log),
            AppContext.BaseDirectory,
            log);

    internal static GatewayRuntime Create(
        HostOptions options,
        HostPaths paths,
        SessionRuntime session,
        string baseDirectory,
        Action<string> log,
        string? userSid = null,
        IGatewayTaskScheduler? scheduler = null,
        ISessionGatewayClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);

        if (paths.PackageFamilyName is null)
        {
            throw new SessionException(
                "OpenClaw is not running from its installed package, so it " +
                "has no identity to manage a gateway with.");
        }

        var persistence = new GatewayPersistenceManager(
            scheduler ?? new SchTasksGatewayScheduler(),
            new GatewayPersistenceOptions(
                UserSid: userSid ?? CurrentUserSid(),
                PackageFamilyName: paths.PackageFamilyName,
                LauncherPath: paths.GatewayLauncherPath,
                StartupFolderPath: Environment.GetFolderPath(
                    Environment.SpecialFolder.Startup),

                // The gateway is given the installation's own state directory
                // rather than inheriting the system directory a logon task
                // starts in, or an interactive caller's current directory.
                WorkingDirectory: paths.StateRoot,
                AliasCommand: ResolveAliasCommand(),
                CommandProcessorPath: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe")),
            log);

        var controller = new GatewayController(
            session.Coordinator,
            client ?? new SessionGatewayClient(session.Backend, log),
            new GatewayStateStore(paths.GatewayStatePath),
            () => CreateStartRequest(options, session, paths),
            log,
            persistence);

        return new GatewayRuntime(controller, persistence, session.HelperPath);
    }

    private static GatewayStartRequest CreateStartRequest(
        HostOptions options,
        SessionRuntime session,
        HostPaths paths)
    {
        string? applicationDirectory = options.PackagedApplicationDirectory;
        if (applicationDirectory is null)
        {
            throw new SessionException(
                "The packaged OpenClaw application was not found, so the " +
                "gateway cannot be started.");
        }

        return new GatewayStartRequest(
            HelperPath: session.HelperPath,
            NodePath: NodeRuntimeResolver.ResolveAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .ExecutablePath,
            ApplicationDirectory: applicationDirectory,
            WorkingDirectory: paths.StateRoot,
            Port: GatewayController.DefaultPort);
    }

    /// <summary>
    /// The SID, not the account name: it is unique across local, domain, and
    /// Entra accounts, and it survives a rename.
    /// </summary>
    private static string CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new SessionException(
                "The signed-in user has no security identifier, so a per-user " +
                "logon task cannot be registered.");
    }

    /// <summary>
    /// The control command the generated launcher invokes.
    /// </summary>
    /// <remarks>
    /// The installed alias is preferred so the launcher survives a package
    /// upgrade moving the executable; the running executable is the fallback
    /// for an unpackaged installation.
    /// </remarks>
    private static string ResolveAliasCommand()
    {
        string alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "clawctl.exe");

        return File.Exists(alias)
            ? alias
            : Environment.ProcessPath
                ?? throw new SessionException(
                    "OpenClaw cannot determine its own path, so it cannot " +
                    "write a launcher that starts the gateway at sign-in.");
    }
}
