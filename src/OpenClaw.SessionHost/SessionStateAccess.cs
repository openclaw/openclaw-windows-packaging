using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

internal sealed class SessionStateAccess(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string JournalPath => Path.Combine(DirectoryPath, "activation.json");

    public static SessionStateAccess ForAgent() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClawGatewayMSIX", "state-transfer"));

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
