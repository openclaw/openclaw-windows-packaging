using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// Assembles the session stack from the running installation.
/// </summary>
/// <remarks>
/// Composition lives here rather than in <c>Program</c> so both the agent and
/// control entry points build the same coordinator, over the same state root
/// and the same lifecycle lock, for the same package identity.
/// </remarks>
public sealed class SessionRuntime
{
    /// <summary>
    /// Directory under the application base holding the guest helper, staged
    /// per architecture exactly like the MXC runtime.
    /// </summary>
    public const string HelperDirectoryName = "session-host";

    public const string HelperFileName = "openclaw-session-host.exe";

    private SessionRuntime(
        SessionCoordinator coordinator,
        SessionExecutor executor,
        IMxcSessionClient backend,
        string helperPath,
        string applicationId)
    {
        Coordinator = coordinator;
        Executor = executor;
        Backend = backend;
        HelperPath = helperPath;
        ApplicationId = applicationId;
    }

    public SessionCoordinator Coordinator { get; }

    public SessionExecutor Executor { get; }

    /// <summary>
    /// The one backend instance this process uses, so the gateway and ordinary
    /// invocations cannot end up talking to differently configured clients.
    /// </summary>
    public IMxcSessionClient Backend { get; }

    public string HelperPath { get; }

    public string ApplicationId { get; }

    public static SessionRuntime Create(Action<string> log) =>
        Create(
            HostPaths.Create(),
            MxcRuntimeLocator.Locate,
            AppContext.BaseDirectory,
            log);

    internal static SessionRuntime Create(
        HostPaths paths,
        Func<MxcRuntimeLocation> locateRuntime,
        string baseDirectory,
        Action<string> log,
        IMxcSessionClient? backend = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        if (paths.PackageFamilyName is null)
        {
            throw new SessionException(
                "OpenClaw is not running from its installed package, so it " +
                "has no identity to provision an isolated session with.");
        }

        string applicationId =
            PackageIdentity.ToApplicationId(paths.PackageFamilyName);
        IMxcSessionClient client = backend ?? new LazyMxcSessionClient(
            () => new MxcCliSessionClient(locateRuntime()));

        var coordinator = new SessionCoordinator(
            client,
            new SessionStateStore(paths.SessionStatePath),
            new NamedSessionLock(applicationId),
            applicationId,
            log);

        return new SessionRuntime(
            coordinator,
            new SessionExecutor(client, log),
            client,
            ResolveHelperPath(baseDirectory),
            applicationId);
    }

    internal static string ResolveHelperPath(string baseDirectory) =>
        Path.Combine(
            baseDirectory,
            HelperDirectoryName,
            MxcRuntimeLocator.CurrentArchitectureName(),
            HelperFileName);
}
