namespace OpenClaw.Launcher.Gateway;

/// <summary>Which mechanism is actually restarting the gateway at logon.</summary>
public enum GatewayPersistenceLane
{
    /// <summary>Nothing is configured to restart the gateway.</summary>
    None,

    /// <summary>The registered logon task, which is the intended lane.</summary>
    TaskScheduler,

    /// <summary>
    /// The Startup folder. Permitted only when the task could not be
    /// registered, and always reported, never a silent substitute.
    /// </summary>
    StartupFolderFallback,
}

public enum GatewayPersistenceState
{
    /// <summary>Logon recovery is not configured.</summary>
    NotInstalled,

    /// <summary>Logon recovery is configured and matches what this build registers.</summary>
    Ready,

    /// <summary>
    /// Something is configured but differs from, or cannot be reconciled with,
    /// what this build registers. The user is told which command repairs it.
    /// </summary>
    ActionRequired,

    /// <summary>
    /// The configured state could not be read. Reported as unknown rather than
    /// guessed in either direction.
    /// </summary>
    Unknown,
}

public sealed record GatewayPersistenceStatus(
    GatewayPersistenceState State,
    GatewayPersistenceLane Lane,
    string Message,
    string? Detail = null,
    string? Remediation = null);

public sealed record GatewayPersistenceInstallResult(
    GatewayPersistenceState State,
    GatewayPersistenceLane Lane,
    string Message,
    bool Changed,
    string? Detail = null,
    string? Remediation = null);

public sealed record GatewayPersistenceRemovalResult(
    bool Succeeded,
    bool Changed,
    string Message,
    string? Detail = null);

/// <summary>
/// Everything the persistence manager needs about this user and installation.
/// </summary>
/// <param name="UserSid">
/// The owning user's SID. The identifier is the SID and not the account name
/// because Task Scheduler names are machine-wide, SIDs are unique across local,
/// domain, and Entra accounts, and a SID survives an account rename.
/// </param>
/// <param name="AliasCommand">
/// The control command the launcher invokes. Held as configuration so the
/// launcher file can be regenerated without re-registering the task.
/// </param>
public sealed record GatewayPersistenceOptions(
    string UserSid,
    string PackageFamilyName,
    string LauncherPath,
    string StartupFolderPath,
    string WorkingDirectory,
    string AliasCommand,
    string CommandProcessorPath);
