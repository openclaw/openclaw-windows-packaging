using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Registers, inspects, and removes this installation's logon recovery.
/// </summary>
/// <remarks>
/// <para>
/// Both lanes start the package's windowless sign-in helper through its
/// package-qualified execution alias, which survives package updates. The
/// intended lane is a logon task that runs the helper. Only when Task
/// Scheduler refuses the registration is a Startup-folder shortcut to the same
/// helper written instead, and that fallback is always reported. Neither lane
/// runs a script host or a console, and nothing is generated in the state
/// directory.
/// </para>
/// <para>
/// Earlier generations ran a generated CMD or Windows Script Host launcher.
/// Their files carry a generator marker; an explicit install removes them once
/// nothing it could not replace references them, and an uninstall removes them
/// too. A file without the marker is never treated as recovery.
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
    public const string RepairCommand = "clawctl setup";

    /// <summary>The package application that is the windowless sign-in helper.</summary>
    public const string HelperApplicationId = "GatewayRecovery";

    public const string HelperExecutable = "openclaw-gateway-recovery.exe";

    /// <summary>
    /// The shortcut's description, which Explorer shows as its comment. Only a
    /// shortcut carrying it is treated as this installation's, so a same-named
    /// shortcut someone else made is reported rather than replaced.
    /// </summary>
    internal const string StartupLinkDescription =
        "Starts the OpenClaw gateway when you sign in. (openclaw-gateway-recovery v1)";

    private readonly IGatewayTaskScheduler _scheduler;
    private readonly GatewayPersistenceOptions _options;
    private readonly GatewayTaskIdentity _identity;
    private readonly Action<string> _log;
    private readonly Func<string, string?> _resolveUserSid;

    public GatewayPersistenceManager(
        IGatewayTaskScheduler scheduler,
        GatewayPersistenceOptions options,
        Action<string>? log = null,
        Func<string, string?>? resolveUserSid = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.StartupLinks);
        if (!Path.IsPathFullyQualified(options.HelperPath))
        {
            throw new ArgumentException(
                $"'{options.HelperPath}' is not a fully qualified path.",
                nameof(options));
        }

        _scheduler = scheduler;
        _options = options;
        _identity = GatewayTaskIdentity.Create(
            options.PackageFamilyName,
            options.UserSid);
        _log = log ?? (_ => { });
        _resolveUserSid = resolveUserSid ?? (_ => null);
        _ = LegacyLauncherPath;
        _ = FallbackPath;
    }

    public string TaskName => _identity.Name;

    /// <summary>
    /// The Startup-folder shortcut this installation owns. Scoped by package
    /// family name so the public and internal packages cannot overwrite each
    /// other's fallback in a folder they both write to.
    /// </summary>
    public string FallbackPath => ResolveOwnedFilePath(
        _options.StartupFolderPath,
        $"{GatewayTaskIdentity.DisplayName} {_options.PackageFamilyName}.lnk");

    private string LegacyLauncherPath => ResolveOwnedFilePath(
        _options.StateDirectory,
        _options.LegacyLauncherPath);

    // Every file an earlier generation could have written: the Windows Script
    // Host launcher, the activation script both generations called, the CMD
    // launcher, and the Startup-folder WSF and CMD files.
    private string[] LegacyFiles =>
    [
        LegacyLauncherPath,
        Path.ChangeExtension(LegacyLauncherPath, ".ps1"),
        Path.ChangeExtension(LegacyLauncherPath, ".cmd"),
        Path.ChangeExtension(FallbackPath, ".wsf"),
        Path.ChangeExtension(FallbackPath, ".cmd"),
    ];

    public async Task<GatewayPersistenceStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        GatewayTaskProbe probe = await _scheduler.QueryAsync(
            _identity.Name,
            cancellationToken).ConfigureAwait(false);

        if (probe.Presence == GatewayTaskPresence.Unreadable)
        {
            _log($"The logon task '{_identity.Name}' could not be read: {DiagnosticFailure.SingleLine(probe.Detail ?? "no detail was reported.")}");
            return new GatewayPersistenceStatus(
                GatewayPersistenceState.Unknown,
                GatewayPersistenceLane.None,
                "Logon recovery could not be read.",
                probe.Detail,
                RepairCommand);
        }

        GatewayPersistenceStatus lane = probe.Presence == GatewayTaskPresence.Missing
            ? DescribeFallback()
            : DescribeTask(probe.Snapshot!);

        // Install removes earlier generations' files only once the lane that
        // replaces them is in place, and reports a removal it could not finish,
        // so status reports the same leftovers and the same repair.
        return lane.State is GatewayPersistenceState.Ready or GatewayPersistenceState.NotInstalled &&
            DescribeLegacyFiles(lane.Lane) is { } legacy
                ? legacy
                : lane;
    }

    public async Task<GatewayPersistenceInstallResult> InstallAsync(
        CancellationToken cancellationToken)
    {
        // A task or shortcut naming a missing helper fails at every sign-in
        // with nothing to show for it, so nothing is registered or written.
        if (DescribeUnavailableHelper() is { } unavailable)
        {
            _log($"Logon recovery was not configured: {unavailable}");
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.None,
                "Logon recovery was not configured because the sign-in helper is unavailable.",
                Changed: false,
                unavailable,
                RepairCommand);
        }

        GatewayTaskProbe probe = await _scheduler.QueryAsync(
            _identity.Name,
            cancellationToken).ConfigureAwait(false);

        // An unreadable probe is not a missing task. Registering anyway is an
        // unbounded retry: the write is as likely to be refused as the read was.
        if (probe.Presence == GatewayTaskPresence.Unreadable)
        {
            _log(
                $"The logon task '{_identity.Name}' could not be read, so it was " +
                $"left unchanged: {DiagnosticFailure.SingleLine(probe.Detail ?? "no detail was reported.")}");
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Unknown,
                GatewayPersistenceLane.None,
                "Logon recovery could not be read, so it was left unchanged.",
                Changed: false,
                probe.Detail,
                RepairCommand);
        }

        if (probe.Presence == GatewayTaskPresence.Present &&
            DescribeDrift(probe.Snapshot!) is null)
        {
            return CompleteMigration(new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.",
                Changed: false));
        }

        GatewayTaskOperation registration = await _scheduler.RegisterAsync(
            _identity.Name,
            GatewayTaskDefinition.CreateXml(DesiredSnapshot(), _identity.Name),
            cancellationToken).ConfigureAwait(false);

        if (registration.Succeeded)
        {
            _log($"Registered the logon task '{_identity.Name}'.");
            // A successful schtasks write is not evidence that its normalized
            // principal and trigger match. Keep any fallback until read-back proves it.
            GatewayPersistenceStatus verified = await VerifyRegistrationAsync(cancellationToken)
                .ConfigureAwait(false);
            if (verified.State != GatewayPersistenceState.Ready)
            {
                _log($"Registered logon recovery could not be verified: {DiagnosticFailure.SingleLine(verified.Detail ?? verified.Message)}");
                return new GatewayPersistenceInstallResult(
                    verified.State == GatewayPersistenceState.Unknown
                        ? GatewayPersistenceState.Unknown
                        : GatewayPersistenceState.ActionRequired,
                    GatewayPersistenceLane.TaskScheduler,
                    "The logon task was registered, but its recovery configuration could not be verified.",
                    Changed: true,
                    verified.Detail ?? verified.Message,
                    RepairCommand);
            }

            return CompleteMigration(new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.",
                Changed: true));
        }

        _log(
            $"The logon task '{_identity.Name}' could not be registered; trying the " +
            $"Startup-folder fallback: {DiagnosticFailure.SingleLine(registration.Detail ?? "no detail was reported.")}");
        GatewayPersistenceInstallResult fallback = InstallFallback(registration.Detail);
        if (probe.Presence == GatewayTaskPresence.Present)
        {
            // The unchanged task may still run an earlier launcher. A new
            // fallback cannot retire files that the existing task still uses.
            return fallback with
            {
                State = GatewayPersistenceState.ActionRequired,
                Detail = Combine(fallback.Detail,
                    "The existing logon task could not be replaced. Its previous recovery " +
                    "files were retained until migration can be completed."),
                Remediation = RepairCommand
            };
        }

        return CompleteMigration(fallback);
    }

    public async Task<GatewayPersistenceRemovalResult> UninstallAsync(
        CancellationToken cancellationToken)
    {
        GatewayTaskOperation deletion = await _scheduler.DeleteAsync(
            _identity.Name,
            cancellationToken).ConfigureAwait(false);

        GatewayGeneratedFileRemoval fallback = RemoveStartupLink();
        GatewayGeneratedFileRemoval legacy = RemoveLegacyFiles();
        string? detail = Combine(deletion.Detail, fallback.Detail, legacy.Detail);

        if (!deletion.Succeeded || detail is not null)
        {
            _log($"Logon recovery could not be fully removed: {DiagnosticFailure.SingleLine(detail ?? "no detail was reported.")}");
            return new GatewayPersistenceRemovalResult(
                Succeeded: false,
                fallback.Changed || legacy.Changed,
                "Logon recovery could not be fully removed.",
                detail);
        }

        return new GatewayPersistenceRemovalResult(
            Succeeded: true,
            fallback.Changed || legacy.Changed,
            "Logon recovery is removed.");
    }

    private GatewayPersistenceStatus DescribeTask(GatewayTaskSnapshot snapshot)
    {
        if (DescribeDrift(snapshot) is { } drift)
        {
            return new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is registered but does not match this installation.",
                drift,
                RepairCommand);
        }

        return DescribeUnavailableHelper() is { } unavailable
            ? new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.TaskScheduler,
                "The logon task is registered, but the sign-in helper it starts is unavailable.",
                unavailable,
                RepairCommand)
            : new GatewayPersistenceStatus(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.");
    }

    private GatewayPersistenceStatus DescribeFallback()
    {
        if (!StartupLinkMatches())
        {
            return new GatewayPersistenceStatus(
                GatewayPersistenceState.NotInstalled,
                GatewayPersistenceLane.None,
                "Logon recovery is not configured.",
                null,
                RepairCommand);
        }

        return DescribeUnavailableHelper() is { } unavailable
            ? new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.StartupFolderFallback,
                "The Startup-folder shortcut is configured, but the sign-in helper it starts is unavailable.",
                unavailable,
                RepairCommand)
            : new GatewayPersistenceStatus(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.StartupFolderFallback,
                "Logon recovery is configured through a Startup-folder shortcut.",
                "The logon task is not registered, so a Startup-folder shortcut starts " +
                "the gateway. It runs later than the task and only for interactive sign-ins.",
                RepairCommand);
    }

    private GatewayPersistenceStatus? DescribeLegacyFiles(GatewayPersistenceLane lane)
    {
        List<string> remaining = FindOwnedLegacyFiles();
        if (remaining.Count == 0)
        {
            return null;
        }

        string files = string.Join(", ", remaining.Select(path => $"'{path}'"));
        bool runsAtSignIn = remaining.Any(path => Path.GetExtension(path) is ".wsf" ||
            string.Equals(path, Path.ChangeExtension(FallbackPath, ".cmd"), StringComparison.OrdinalIgnoreCase));
        return runsAtSignIn
            ? new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                lane == GatewayPersistenceLane.None ? GatewayPersistenceLane.StartupFolderFallback : lane,
                "An earlier Startup-folder launcher is still installed.",
                $"{files} {(remaining.Count == 1 ? "is" : "are")} left from an earlier version and " +
                "can open a console window or a Windows Script Host prompt at sign-in. " +
                $"Run '{RepairCommand}' to replace it with the windowless sign-in helper.",
                RepairCommand)
            : new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                lane,
                "Files from an earlier logon recovery remain.",
                $"Run '{RepairCommand}' to remove {files}.",
                RepairCommand);
    }

    private string? DescribeUnavailableHelper() =>
        File.Exists(_options.HelperPath)
            ? null
            : $"'{_options.HelperPath}' does not exist, so nothing would start the gateway " +
              "at sign-in. Repair or reinstall the OpenClaw Gateway package, then run " +
              $"'{RepairCommand}'.";

    private async Task<GatewayPersistenceStatus> VerifyRegistrationAsync(
        CancellationToken cancellationToken)
    {
        GatewayTaskProbe probe = await _scheduler.QueryAsync(
            _identity.Name,
            cancellationToken).ConfigureAwait(false);
        return probe.Presence switch
        {
            GatewayTaskPresence.Present => DescribeTask(probe.Snapshot!),
            GatewayTaskPresence.Unreadable => new GatewayPersistenceStatus(
                GatewayPersistenceState.Unknown,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery could not be read.",
                probe.Detail),
            _ => new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.TaskScheduler,
                "The registered logon task could not be found.")
        };
    }

    // Earlier generations remain recoverable until an explicit install
    // switches the task or fallback. Remove only marked files after that succeeds.
    private GatewayPersistenceInstallResult CompleteMigration(GatewayPersistenceInstallResult result)
    {
        if (result.State != GatewayPersistenceState.Ready)
        {
            return result;
        }

        GatewayGeneratedFileRemoval fallback = result.Lane == GatewayPersistenceLane.TaskScheduler
            ? RemoveStartupLink()
            : new GatewayGeneratedFileRemoval(false, null);
        GatewayGeneratedFileRemoval legacy = RemoveLegacyFiles();
        string? detail = Combine(fallback.Detail, legacy.Detail);
        return result with
        {
            State = detail is null ? result.State : GatewayPersistenceState.ActionRequired,
            Changed = result.Changed || fallback.Changed || legacy.Changed,
            Detail = Combine(result.Detail, detail),
            Remediation = detail is null ? result.Remediation : RepairCommand
        };
    }

    private GatewayGeneratedFileRemoval RemoveLegacyFiles()
    {
        bool changed = false;
        string? detail = null;
        foreach (string path in LegacyFiles)
        {
            GatewayGeneratedFileRemoval removal = RemoveGeneratedFile(path);
            changed |= removal.Changed;
            detail = Combine(detail, removal.Detail);
        }

        return new GatewayGeneratedFileRemoval(changed, detail);
    }

    // An unreadable file is treated as owned: install reports the same file
    // as a failed cleanup, so status and install stay consistent.
    private List<string> FindOwnedLegacyFiles()
    {
        List<string> owned = [];
        foreach (string path in LegacyFiles)
        {
            try
            {
                if (IsGenerated(path))
                {
                    owned.Add(path);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                owned.Add(path);
            }
        }

        return owned;
    }

    private GatewayTaskSnapshot DesiredSnapshot() =>
        GatewayTaskDefinition.CreateSnapshot(
            _options.UserSid,
            _options.HelperPath,
            _options.LogonTriggerUserSid);

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
        else if (!MatchesUserSid(actual.LogonTriggerUserId, desired.LogonTriggerUserId))
        {
            differences.Add(
                $"The logon trigger is scoped to a different user. " +
                $"Expected '{desired.LogonTriggerUserId}', observed '{actual.LogonTriggerUserId}'.");
        }

        if (!Same(actual.UserId, desired.UserId))
        {
            differences.Add(
                $"The task runs as a different user. " +
                $"Expected '{desired.UserId}', observed '{actual.UserId}'.");
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
        // Creating the folder from inside the package would land in the
        // package's private copy of AppData, which Explorer never reads at
        // sign-in. Only an existing Startup folder is written to.
        if (!Directory.Exists(_options.StartupFolderPath))
        {
            _log($"The Startup folder '{_options.StartupFolderPath}' does not exist, so no shortcut was written.");
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.None,
                "Logon recovery could not be configured.",
                Changed: false,
                Combine(taskDetail, $"The Startup folder '{_options.StartupFolderPath}' does not exist."),
                RepairCommand);
        }

        bool changed;
        try
        {
            changed = WriteStartupLink();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _log(
                $"The Startup-folder shortcut '{FallbackPath}' could not be written: " +
                DiagnosticFailure.Describe(exception));
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.None,
                "Logon recovery could not be configured.",
                Changed: false,
                Combine(taskDetail, $"'{FallbackPath}': {exception.Message}"),
                RepairCommand);
        }

        _log($"Registered the Startup-folder shortcut at '{FallbackPath}'.");
        return new GatewayPersistenceInstallResult(
            GatewayPersistenceState.Ready,
            GatewayPersistenceLane.StartupFolderFallback,
            "Logon recovery is configured through a Startup-folder shortcut.",
            changed,
            Combine(
                taskDetail,
                "The logon task could not be registered, so a Startup-folder " +
                "shortcut starts the gateway instead. It runs later than the task " +
                "and only for interactive sign-ins."),
            RepairCommand);
    }

    private StartupLink DesiredStartupLink() =>
        new(_options.HelperPath, string.Empty, StartupLinkDescription);

    private bool WriteStartupLink()
    {
        StartupLink? existing = _options.StartupLinks.Read(FallbackPath);
        if (existing is not null)
        {
            if (Matches(existing))
            {
                return false;
            }

            if (!IsOwned(existing))
            {
                throw new IOException(
                    $"'{FallbackPath}' exists but was not created by OpenClaw.");
            }
        }

        _options.StartupLinks.Write(FallbackPath, DesiredStartupLink());
        return true;
    }

    private bool StartupLinkMatches()
    {
        try
        {
            return _options.StartupLinks.Read(FallbackPath) is { } link && Matches(link);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool Matches(StartupLink link) =>
        IsOwned(link) &&
        Same(link.TargetPath, _options.HelperPath) &&
        link.Arguments.Length == 0;

    private static bool IsOwned(StartupLink link) =>
        string.Equals(link.Description, StartupLinkDescription, StringComparison.Ordinal);

    private GatewayGeneratedFileRemoval RemoveStartupLink()
    {
        // Only a shortcut this installation created is deleted. One that cannot
        // be read is reported rather than guessed to be someone else's.
        try
        {
            if (_options.StartupLinks.Read(FallbackPath) is not { } link || !IsOwned(link))
            {
                return new GatewayGeneratedFileRemoval(false, null);
            }

            (_options.DeleteFile ?? File.Delete)(FallbackPath);
            return new GatewayGeneratedFileRemoval(true, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _log(
                $"The Startup-folder shortcut '{FallbackPath}' could not be removed: " +
                DiagnosticFailure.Describe(exception));
            return new GatewayGeneratedFileRemoval(
                false,
                $"Could not remove the Startup-folder shortcut '{FallbackPath}': {exception.Message}");
        }
    }

    // The one ownership test for earlier generations' files: only a file
    // carrying a generator marker is ours. Status and removal share it so they
    // cannot disagree about a legacy file.
    private static bool IsGenerated(string path) =>
        File.Exists(path) &&
        GatewayLauncherScript.LooksGenerated(File.ReadAllText(path));

    private GatewayGeneratedFileRemoval RemoveGeneratedFile(string path)
    {
        // Only a file this installation generated is deleted. A same-named file
        // someone else placed there is left alone.
        try
        {
            if (!IsGenerated(path))
            {
                return new GatewayGeneratedFileRemoval(false, null);
            }

            (_options.DeleteFile ?? File.Delete)(path);
            return new GatewayGeneratedFileRemoval(true, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _log(
                $"The generated recovery file '{path}' could not be removed: " +
                DiagnosticFailure.Describe(exception));
            return new GatewayGeneratedFileRemoval(
                false,
                $"Could not remove generated recovery file '{path}': {exception.Message}");
        }
    }

    private bool MatchesUserSid(string userId, string desiredSid) =>
        Same(userId, desiredSid) ||
        Same(_resolveUserSid(userId) ?? string.Empty, desiredSid);

    private static string ResolveOwnedFilePath(string directory, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(
            Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        string? parent = Path.GetDirectoryName(candidate);
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{path}' is not a direct child of '{directory}'.",
                nameof(path));
        }

        TrustedPath.EnsureNoReparsePoints(root, candidate);
        return candidate;
    }

    private sealed record GatewayGeneratedFileRemoval(bool Changed, string? Detail);

    private static string? Combine(string? first, string? second) =>
        (string.IsNullOrWhiteSpace(first), string.IsNullOrWhiteSpace(second)) switch
        {
            (true, true) => null,
            (true, false) => second,
            (false, true) => first,
            _ => $"{first} {second}"
        };

    private static string? Combine(string? first, string? second, string? third) =>
        Combine(first, Combine(second, third));

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
