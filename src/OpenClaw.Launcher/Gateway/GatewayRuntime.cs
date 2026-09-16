using System.Runtime.InteropServices;
using System.Security.Principal;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>Assembles gateway management from the running installation.</summary>
internal sealed partial class GatewayRuntime
{
    private static readonly Guid StartupFolderId =
        new("B97D20BB-F46A-4C97-BA10-5E3608430854");

    private readonly HostPaths _paths;
    private readonly SessionRuntime _session;
    private readonly TimeProvider _clock;

    private GatewayRuntime(
        GatewayController controller,
        string helperPath,
        HostPaths paths,
        SessionRuntime session,
        TimeProvider clock)
    {
        Controller = controller;
        HelperPath = helperPath;
        _paths = paths;
        _session = session;
        _clock = clock;
    }

    public GatewayController Controller { get; }

    public string HelperPath { get; }

    internal HostPaths Paths => _paths;

    internal string? GetRecordedWorkspacePath() =>
        _session.Coordinator.GetRecordedStatus().Record?.WorkspacePath;

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
        string logonUserSid = ResolveUserSid(
            $@"{Environment.UserDomainName}\{Environment.UserName}")
            ?? throw new SessionException(
                "The signed-in user's logon identity could not be resolved, so gateway recovery cannot be configured.");

        return new GatewayPersistenceManager(
            new SchTasksGatewayScheduler(),
            new GatewayPersistenceOptions(
                userSid,
                packageFamilyName,
                paths.GatewayLauncherPath,
                GetStartupFolderPath(),
                paths.StateRoot,
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                LogonTriggerUserSid: logonUserSid),
            log,
            ResolveUserSid);
    }

    private static string GetStartupFolderPath()
    {
        int result = SHGetKnownFolderPath(
            StartupFolderId,
            flags: 0,
            token: IntPtr.Zero,
            out IntPtr path);
        if (result < 0)
        {
            throw new SessionException(
                $"Windows could not resolve the Startup folder " +
                $"(HRESULT 0x{result:X8}).");
        }

        try
        {
            return Marshal.PtrToStringUni(path)
                ?? throw new SessionException(
                    "Windows returned an empty Startup folder path.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        in Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);

    private static string? ResolveUserSid(string accountName)
    {
        try
        {
            return new NTAccount(accountName)
                .Translate(typeof(SecurityIdentifier))
                .Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
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
        Action<string> log,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);

        return new GatewayRuntime(
            CreateController(options, paths, session, log),
            session.HelperPath,
            paths,
            session,
            clock ?? TimeProvider.System);
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
        Task<GatewayStartRequest> CreateRequestAsync(CancellationToken cancellationToken)
        {
            string applicationDirectory = options.PackagedApplicationDirectory
                ?? throw new SessionException(
                    "The packaged OpenClaw application was not found, so the gateway cannot be started.");
            SessionRecord sessionRecord = session.RequireSetup();
            GatewayLaunchConfiguration launch = ResolveLaunchConfiguration(
                configuration,
                sessionRecord,
                Environment.GetEnvironmentVariable);
            string archivePath = options.PackagedNodeArchivePath
                ?? throw new SessionException(
                    "The packaged Node.js runtime archive was not found.");
            return Task.FromResult(new GatewayStartRequest(
                session.HelperPath,
                session.RequireAgentNodePath(archivePath),
                applicationDirectory,
                launch.Port)
            {
                WorkingDirectory = launch.WorkingDirectory
            });
        }

        return new GatewayController(
            session.Coordinator,
            new SessionGatewayClient(
                session.Backend,
                log,
                isCurrentRecord: IsCurrentSessionRecord),
            session.GatewayState,
            CreateRequestAsync,
            log,
            session.RequireSetup,
            session.LifecycleLock);

        bool IsCurrentSessionRecord(SessionRecord record)
        {
            SessionStatus status = session.Coordinator.GetRecordedStatus();
            return status.Record is not null &&
                string.Equals(status.Record.SandboxId, record.SandboxId, StringComparison.Ordinal) &&
                string.Equals(status.Record.Generation, record.Generation, StringComparison.Ordinal);
        }
    }

    internal static GatewayLaunchConfiguration ResolveLaunchConfiguration(
        GatewayConfigurationStore configuration,
        SessionRecord sessionRecord,
        Func<string, string?> environmentVariable)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sessionRecord);
        ArgumentNullException.ThrowIfNull(environmentVariable);

        string workspacePath = sessionRecord.WorkspacePath
            ?? throw new SessionException(
                "The isolated session has no shared workspace for the gateway.");
        return configuration.Resolve(workspacePath, environmentVariable);
    }
}
