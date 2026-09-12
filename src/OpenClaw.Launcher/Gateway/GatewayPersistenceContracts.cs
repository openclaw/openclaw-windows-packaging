namespace OpenClaw.Launcher.Gateway;

/// <summary>Which mechanism is actually restarting the gateway at logon.</summary>
internal enum GatewayPersistenceLane
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

internal enum GatewayPersistenceState
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

internal sealed record GatewayPersistenceStatus(
    GatewayPersistenceState State,
    GatewayPersistenceLane Lane,
    string Message,
    string? Detail = null,
    string? Remediation = null);

internal sealed record GatewayPersistenceInstallResult(
    GatewayPersistenceState State,
    GatewayPersistenceLane Lane,
    string Message,
    bool Changed,
    string? Detail = null,
    string? Remediation = null);

internal sealed record GatewayPersistenceRemovalResult(
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
/// <param name="PackageFamilyName">
/// Scopes the task name to this installation, so the public and internal
/// packages can never overwrite or delete each other's registration.
/// </param>
/// <param name="LauncherPath">The generated script both lanes invoke.</param>
/// <param name="StartupFolderPath">
/// Where the explicitly reported Startup-folder fallback is written.
/// </param>
/// <param name="WorkingDirectory">
/// The directory the gateway is given, rather than the system directory a
/// logon task would otherwise inherit.
/// </param>
/// <param name="AliasCommand">
/// The control command the launcher invokes. Held as configuration so the
/// launcher file can be regenerated without re-registering the task.
/// </param>
/// <param name="CommandProcessorPath">
/// The absolute path to the inbox command processor the task runs.
/// </param>
internal sealed record GatewayPersistenceOptions(
    string UserSid,
    string PackageFamilyName,
    string LauncherPath,
    string StartupFolderPath,
    string WorkingDirectory,
    string AliasCommand,
    string CommandProcessorPath);
