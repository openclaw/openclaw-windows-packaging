namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Registers, inspects, and removes this installation's logon recovery.
/// </summary>
/// <remarks>
/// <para>
/// Persistence is three files' worth of state: a rewritable launcher script, a
/// logon task that calls it, and an optional Startup-folder fallback that calls
/// the same script. Only the task is the intended lane.
/// </para>
/// <para>
/// This type never elevates and never repairs behind the user's back. An
/// explicit <c>install</c> rewrites what it owns; <c>status</c> only reports,
/// and names the command that repairs what it found.
/// </para>
/// </remarks>
internal sealed class GatewayPersistenceManager
{
    /// <summary>The command a reported problem tells the user to run.</summary>
    public const string RepairCommand = "clawctl gateway-service install";

    private readonly IGatewayTaskScheduler _scheduler;
    private readonly GatewayPersistenceOptions _options;
    private readonly GatewayTaskIdentity _identity;
    private readonly Action<string> _log;

    public GatewayPersistenceManager(
        IGatewayTaskScheduler scheduler,
        GatewayPersistenceOptions options,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(options);

        _scheduler = scheduler;
        _options = options;
        _identity = GatewayTaskIdentity.Create(
            options.PackageFamilyName,
            options.UserSid);
        _log = log ?? (_ => { });
    }

    public string TaskName => _identity.Name;

    /// <summary>
    /// The Startup-folder file this installation owns. Scoped by package family
    /// name so the public and internal packages cannot overwrite each other's
    /// fallback in a folder they both write to.
    /// </summary>
    public string FallbackPath => Path.Combine(
        _options.StartupFolderPath,
        $"{GatewayTaskIdentity.DisplayName} {_options.PackageFamilyName}.cmd");

    public async Task<GatewayPersistenceStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        GatewayTaskProbe probe = await _scheduler.QueryAsync(
            _identity.Name,
            cancellationToken).ConfigureAwait(false);

        if (probe.Presence == GatewayTaskPresence.Unreadable)
        {
            return new GatewayPersistenceStatus(
                GatewayPersistenceState.Unknown,
                GatewayPersistenceLane.None,
                "Logon recovery could not be read.",
                probe.Detail,
                RepairCommand);
        }

        bool fallbackPresent = FallbackMatches();

        if (probe.Presence == GatewayTaskPresence.Missing)
        {
            return fallbackPresent
                ? new GatewayPersistenceStatus(
                    GatewayPersistenceState.Ready,
                    GatewayPersistenceLane.StartupFolderFallback,
                    "Logon recovery is configured through the Startup folder.",
                    "The logon task is not registered, so the Startup-folder " +
                    "fallback is in use. It runs later than the task and only " +
                    "for interactive sign-ins.",
                    RepairCommand)
                : new GatewayPersistenceStatus(
                    GatewayPersistenceState.NotInstalled,
                    GatewayPersistenceLane.None,
                    "Logon recovery is not configured.",
                    null,
                    RepairCommand);
        }

        string? drift = DescribeDrift(probe.Snapshot!);
        if (drift is not null)
        {
            return new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is registered but does not match this installation.",
                drift,
                RepairCommand);
        }

        if (!LauncherMatches())
        {
            return new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.TaskScheduler,
                "The logon task is registered but its launcher is missing or modified.",
                $"'{_options.LauncherPath}' does not contain the expected " +
                "generated launcher, so the task would not start the gateway.",
                RepairCommand);
        }

        return new GatewayPersistenceStatus(
            GatewayPersistenceState.Ready,
            GatewayPersistenceLane.TaskScheduler,
            "Logon recovery is configured.");
    }

    public async Task<GatewayPersistenceInstallResult> InstallAsync(
        CancellationToken cancellationToken)
    {
        bool changed;
        try
        {
            changed = WriteLauncher();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.None,
                "The gateway launcher could not be written.",
                Changed: false,
                $"'{_options.LauncherPath}': {exception.Message}",
                RepairCommand);
        }

        GatewayTaskProbe probe = await _scheduler.QueryAsync(
            _identity.Name,
            cancellationToken).ConfigureAwait(false);

        // An unreadable probe is not a missing task. Registering anyway is an
        // unbounded retry: the write is as likely to be refused as the read was.
        if (probe.Presence == GatewayTaskPresence.Unreadable)
        {
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Unknown,
                GatewayPersistenceLane.None,
                "Logon recovery could not be read, so it was left unchanged.",
                changed,
                probe.Detail,
                RepairCommand);
        }

        bool alreadyCorrect =
            probe.Presence == GatewayTaskPresence.Present &&
            DescribeDrift(probe.Snapshot!) is null;

        if (alreadyCorrect)
        {
            RemoveFallback();
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.",
                changed);
        }

        GatewayTaskOperation registration = await _scheduler.RegisterAsync(
            _identity.Name,
            GatewayTaskDefinition.CreateXml(DesiredSnapshot(), _identity.Name),
            cancellationToken).ConfigureAwait(false);

        if (registration.Succeeded)
        {
            _log($"Registered the logon task '{_identity.Name}'.");
            RemoveFallback();
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.",
                Changed: true);
        }

        return InstallFallback(registration.Detail);
    }

    public async Task<GatewayPersistenceRemovalResult> UninstallAsync(
        CancellationToken cancellationToken)
    {
        GatewayTaskOperation deletion = await _scheduler.DeleteAsync(
            _identity.Name,
            cancellationToken).ConfigureAwait(false);

        bool changed = RemoveFallback();
        changed |= RemoveLauncher();

        if (!deletion.Succeeded)
        {
            return new GatewayPersistenceRemovalResult(
                Succeeded: false,
                changed,
                "Logon recovery could not be fully removed.",
                deletion.Detail);
        }

        return new GatewayPersistenceRemovalResult(
            Succeeded: true,
            changed,
            "Logon recovery is removed.");
    }

    private GatewayTaskSnapshot DesiredSnapshot() =>
        GatewayTaskDefinition.CreateSnapshot(
            _options.UserSid,
            _options.CommandProcessorPath,
            _options.LauncherPath);

    private string? DescribeDrift(GatewayTaskSnapshot actual)
    {
        GatewayTaskSnapshot desired = DesiredSnapshot();
        List<string> differences = [];

        if (!actual.Enabled)
        {
            differences.Add("The task is disabled.");
        }

        if (!actual.HasSingleLogonTrigger)
        {
            differences.Add("The task does not have exactly one logon trigger.");
        }
        else if (!actual.LogonTriggerEnabled)
        {
            differences.Add("The logon trigger is disabled.");
        }
        else if (!Same(actual.LogonTriggerUserId, desired.LogonTriggerUserId))
        {
            differences.Add("The logon trigger is scoped to a different user.");
        }

        if (!Same(actual.UserId, desired.UserId))
        {
            differences.Add("The task runs as a different user.");
        }

        if (!Same(actual.LogonType, desired.LogonType))
        {
            differences.Add($"The logon type is '{actual.LogonType}'.");
        }

        if (!Same(actual.RunLevel, desired.RunLevel))
        {
            differences.Add($"The task runs at '{actual.RunLevel}'.");
        }

        if (!Same(actual.MultipleInstancesPolicy, desired.MultipleInstancesPolicy))
        {
            differences.Add(
                "A second sign-in would not be suppressed by the " +
                "duplicate-instance policy.");
        }

        if (actual.DisallowStartIfOnBatteries || actual.StopIfGoingOnBatteries)
        {
            differences.Add("The task is gated on AC power.");
        }

        if (!Same(actual.ExecutionTimeLimit, desired.ExecutionTimeLimit))
        {
            differences.Add(
                $"The task would be terminated after '{actual.ExecutionTimeLimit}'.");
        }

        if (!actual.HasSingleExecAction)
        {
            differences.Add("The task does not have exactly one action.");
        }
        else if (!Same(actual.Command, desired.Command) ||
                 !Same(actual.Arguments, desired.Arguments))
        {
            differences.Add("The task runs a different command.");
        }

        return differences.Count == 0 ? null : string.Join(" ", differences);
    }

    private GatewayPersistenceInstallResult InstallFallback(string? taskDetail)
    {
        try
        {
            Directory.CreateDirectory(_options.StartupFolderPath);
            File.WriteAllText(
                FallbackPath,
                GatewayLauncherScript.CreateFallback(_options.LauncherPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.None,
                "Logon recovery could not be configured.",
                Changed: true,
                Combine(taskDetail, $"'{FallbackPath}': {exception.Message}"),
                RepairCommand);
        }

        _log($"Registered the Startup-folder fallback at '{FallbackPath}'.");
        return new GatewayPersistenceInstallResult(
            GatewayPersistenceState.Ready,
            GatewayPersistenceLane.StartupFolderFallback,
            "Logon recovery is configured through the Startup folder.",
            Changed: true,
            Combine(
                taskDetail,
                "The logon task could not be registered, so the Startup-folder " +
                "fallback was used. It runs later than the task and only for " +
                "interactive sign-ins."),
            RepairCommand);
    }

    private bool WriteLauncher()
    {
        string content = GatewayLauncherScript.Create(
            _options.WorkingDirectory,
            _options.AliasCommand);

        if (File.Exists(_options.LauncherPath) &&
            string.Equals(
                File.ReadAllText(_options.LauncherPath),
                content,
                StringComparison.Ordinal))
        {
            return false;
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(_options.LauncherPath)
            ?? throw new InvalidOperationException(
                $"'{_options.LauncherPath}' has no parent directory."));
        File.WriteAllText(_options.LauncherPath, content);
        return true;
    }

    private bool LauncherMatches()
    {
        try
        {
            return File.Exists(_options.LauncherPath) &&
                   string.Equals(
                       File.ReadAllText(_options.LauncherPath),
                       GatewayLauncherScript.Create(
                           _options.WorkingDirectory,
                           _options.AliasCommand),
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool FallbackMatches()
    {
        try
        {
            return File.Exists(FallbackPath) &&
                   GatewayLauncherScript.LooksGenerated(
                       File.ReadAllText(FallbackPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool RemoveFallback()
    {
        // Only a file this installation generated is deleted. A same-named file
        // someone else placed there is left alone.
        try
        {
            if (!File.Exists(FallbackPath) ||
                !GatewayLauncherScript.LooksGenerated(File.ReadAllText(FallbackPath)))
            {
                return false;
            }

            File.Delete(FallbackPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool RemoveLauncher()
    {
        try
        {
            if (!File.Exists(_options.LauncherPath) ||
                !GatewayLauncherScript.LooksGenerated(
                    File.ReadAllText(_options.LauncherPath)))
            {
                return false;
            }

            File.Delete(_options.LauncherPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Combine(string? first, string? second) =>
        (string.IsNullOrWhiteSpace(first), string.IsNullOrWhiteSpace(second)) switch
        {
            (true, true) => null,
            (true, false) => second,
            (false, true) => first,
            _ => $"{first} {second}"
        };

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
