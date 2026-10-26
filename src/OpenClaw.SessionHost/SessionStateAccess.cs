using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

internal sealed class SessionStateAccess(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string JournalPath => Path.Combine(DirectoryPath, "activation.json");

    /// <summary>
    /// Names an absolute directory that replaces the agent profile's state
    /// directory for this process.
    /// </summary>
    /// <remarks>
    /// Windows resolves the profile's local application data from the registry,
    /// not from <c>LOCALAPPDATA</c>, so a test that starts the real helper
    /// executable has no other way to keep its lock files out of the
    /// developer's real profile. The launcher never sets this.
    /// </remarks>
    internal const string DirectoryVariable = "OPENCLAW_SESSION_STATE_DIRECTORY";

    public static SessionStateAccess ForAgent()
    {
        string? overridden = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrEmpty(overridden))
        {
            return new(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawGatewayMSIX", "state-transfer"));
        }

        // Falling back would silently put the locks where the override was
        // meant to keep them out.
        return Path.IsPathFullyQualified(overridden)
            ? new(overridden)
            : throw new SessionLaunchException(
                $"{DirectoryVariable} must name an absolute directory.");
    }

    public IDisposable EnterReader()
    {
        using IDisposable coordination = Enter("intent.lock", exclusive: true);
        RequireNoPending();
        return Enter("access.lock", exclusive: false);
    }

    public IDisposable EnterWorker() => Enter("worker.lock", exclusive: true);

    public IDisposable EnterMaintenance()
    {
        using IDisposable coordination = Enter("intent.lock", exclusive: true);
        return Enter("access.lock", exclusive: true);
    }

    public IDisposable EnterIntent() => Enter("intent.lock", exclusive: true);

    public void RequireNoPending()
    {
        try
        {
            _ = File.GetAttributes(JournalPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        throw new SessionLaunchException(
            "State activation is pending. Run `clawctl status`, then `clawctl restore --rollback --yes`.");
    }

    private Lease Enter(string name, bool exclusive)
    {
        TrustedPath.EnsureNoReparsePoints(Path.GetPathRoot(DirectoryPath)!, DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        TrustedPath.ValidatedDirectory root =
            TrustedPath.TryOpenValidatedDirectory(DirectoryPath, expectedIdentity: null)
            ?? throw new SessionLaunchException("The agent's state-access directory is unavailable or unsafe.");
        try
        {
            return new Lease(root, TrustedPath.OpenLock(root, name, exclusive));
        }
        catch (IOException exception)
        {
            root.Dispose();
            throw new SessionLaunchException(
                "Another agent process is using OpenClaw state. Let it finish before retrying " +
                "`clawctl restore` or `clawctl restore --rollback`.", exception);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private sealed class Lease(TrustedPath.ValidatedDirectory root, FileStream file) : IDisposable
    {
        public void Dispose()
        {
            file.Dispose();
            root.Dispose();
        }
    }
}
