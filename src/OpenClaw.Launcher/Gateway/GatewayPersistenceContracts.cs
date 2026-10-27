namespace OpenClaw.Launcher.Gateway;

/// <summary>Which mechanism is actually restarting the gateway at logon.</summary>
internal enum GatewayPersistenceLane
{
    /// <summary>Nothing is configured to restart the gateway.</summary>
    None,

    /// <summary>The registered logon task, which is the intended lane.</summary>
    TaskScheduler,

    /// <summary>
    /// A Startup-folder shortcut. Permitted only when the task could not be
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
/// <param name="HelperPath">
/// The package-qualified execution alias of the windowless sign-in helper that
/// both lanes start. It survives package updates, unlike the install directory.
/// </param>
/// <param name="LegacyLauncherPath">
/// The script an earlier generation's task or Startup file ran. It and its
/// <c>.ps1</c> and <c>.cmd</c> siblings are removed once nothing references them.
/// </param>
/// <param name="StartupFolderPath">
/// Where the explicitly reported Startup-folder fallback is written.
/// </param>
/// <param name="StateDirectory">
/// The installation state directory that holds the legacy launcher files.
/// </param>
/// <param name="StartupLinks">Reads and writes the Startup-folder shortcut.</param>
/// <param name="DeleteFile">
/// Optional file-delete seam used to make persistence failures observable in tests.
/// </param>
/// <param name="LogonTriggerUserSid">
/// The interactive user whose logon starts recovery. Defaults to <paramref name="UserSid"/>.
/// </param>
internal sealed record GatewayPersistenceOptions(
    string UserSid,
    string PackageFamilyName,
    string HelperPath,
    string LegacyLauncherPath,
    string StartupFolderPath,
    string StateDirectory,
    IStartupLinkStore StartupLinks,
    Action<string>? DeleteFile = null,
    string? LogonTriggerUserSid = null);
