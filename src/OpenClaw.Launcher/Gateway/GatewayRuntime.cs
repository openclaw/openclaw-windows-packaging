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

    public static GatewayRuntime Create(
        HostOptions options,
        Action<string> log,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _ = resolveNode;

        HostPaths paths = HostPaths.Create();
        if (paths.PackageFamilyName is null)
        {
            throw new SessionException(
                "OpenClaw is not running from its installed package, so it has no identity to manage a gateway with.");
        }

        SessionRuntime session = SessionRuntime.Create(log);
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
            return Task.FromResult(new GatewayStartRequest(
                session.HelperPath,
                session.RequireAgentNodePath(
                    options.PackagedNodeArchivePath
                    ?? throw new SessionException(
                        "The packaged Node.js runtime archive was not found.")),
                applicationDirectory,
                launch.WorkingDirectory ?? sessionRecord.WorkspacePath
                    ?? throw new SessionException(
                        "The isolated session has no shared workspace for the gateway."),
                launch.Port));
        }

        return new GatewayRuntime(
            new GatewayController(
                session.Coordinator,
                new SessionGatewayClient(
                    session.Backend,
                    log,
                    isCurrentRecord: IsCurrentSessionRecord),
                new GatewayStateStore(paths.GatewayStatePath),
                CreateRequestAsync,
                log,
                session.RequireSetup,
                session.LifecycleLock),
            session.HelperPath);

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
