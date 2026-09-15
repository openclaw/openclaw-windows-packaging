namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Identifies this installation's logon task.
/// </summary>
/// <remarks>
/// <para>
/// Task Scheduler names are machine-wide while gateway persistence is per-user
/// and per-installation. A name scoped by neither would let one user's install
/// overwrite another's task and one user's uninstall delete it; a name scoped
/// only by user would let this package and the internal MSIX fight over the
/// same entry.
/// </para>
/// <para>
/// The SID is the identifier, not the account name: it is the only one
/// guaranteed unique across local, domain, and Entra accounts, and it does not
/// change when an account is renamed.
/// </para>
/// </remarks>
internal sealed record GatewayTaskIdentity
{
    public const string DisplayName = "OpenClaw Gateway";

    private GatewayTaskIdentity(string name, string packageFamilyName, string userSid)
    {
        Name = name;
        PackageFamilyName = packageFamilyName;
        UserSid = userSid;
    }

    /// <summary>The Task Scheduler name, without a folder path.</summary>
    public string Name { get; }

    public string PackageFamilyName { get; }

    public string UserSid { get; }

    public static GatewayTaskIdentity Create(string packageFamilyName, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);

        // Task Scheduler treats a backslash as a folder separator, so a name
        // carrying one would silently address a different folder.
        if (packageFamilyName.Contains('\\', StringComparison.Ordinal) ||
            userSid.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A task name component may not contain a backslash.",
                nameof(packageFamilyName));
        }

        return new GatewayTaskIdentity(
            $"{DisplayName} {packageFamilyName} {userSid}",
            packageFamilyName,
            userSid);
    }

    /// <summary>
    /// True when <paramref name="taskName"/> belongs to this installation, for
    /// any user. Used to recognize our own tasks without assuming who
    /// registered them, and never to claim another package's task.
    /// </summary>
    public static bool BelongsToPackage(string taskName, string packageFamilyName) =>
        taskName.StartsWith(
            $"{DisplayName} {packageFamilyName} ",
            StringComparison.OrdinalIgnoreCase);
}
