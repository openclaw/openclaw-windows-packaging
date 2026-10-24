using System.Text;
using OpenClaw.Launcher.Session;

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
    public const string RepairCommand = "clawctl setup";

    private const string ScriptHostDisabledMessage =
        "Windows Script Host is disabled, so logon recovery cannot run.";

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

        _scheduler = scheduler;
        _options = options;
        _identity = GatewayTaskIdentity.Create(
            options.PackageFamilyName,
            options.UserSid);
        _log = log ?? (_ => { });
        _resolveUserSid = resolveUserSid ?? (_ => null);
        _ = LauncherPath;
        _ = FallbackPath;
    }

    public string TaskName => _identity.Name;

    /// <summary>
    /// The Startup-folder file this installation owns. Scoped by package family
    /// name so the public and internal packages cannot overwrite each other's
    /// fallback in a folder they both write to.
    /// </summary>
    public string FallbackPath => ResolveOwnedFilePath(
        _options.StartupFolderPath,
        $"{GatewayTaskIdentity.DisplayName} {_options.PackageFamilyName}.wsf");

    private string LauncherPath => ResolveOwnedFilePath(
        _options.WorkingDirectory,
        _options.LauncherPath);

    private string ActivationScriptPath => ResolveOwnedFilePath(
        _options.WorkingDirectory,
        Path.ChangeExtension(LauncherPath, ".ps1"));

    public async Task<GatewayPersistenceStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        GatewayPersistenceStatus status = await ReadStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        if (status.State == GatewayPersistenceState.Unknown ||
            FindDisabledScriptHostSetting() is not { } disabledBy)
        {
            return status;
        }

        // With the script host disabled, install registers nothing, so a machine
        // with no recovery is reported as needing attention too: status and
        // setup then agree, and status exits nonzero until the host is enabled.
        // A finding that already needs attention keeps its own message, such as a
        // previous console launcher that still works, and gains the prerequisite.
        return status.State == GatewayPersistenceState.ActionRequired
            ? status with { Detail = Combine(status.Detail, DescribeDisabledScriptHost(disabledBy)) }
            : status with
            {
                State = GatewayPersistenceState.ActionRequired,
                Message = ScriptHostDisabledMessage,
                Detail = DescribeDisabledScriptHost(disabledBy),
                Remediation = RepairCommand
            };
    }

    // Reads the task and files only. Install verifies a fresh registration with
    // this: it already checked the script host, and a verification failure
    // should describe the registration, not the registry.
    private async Task<GatewayPersistenceStatus> ReadStatusAsync(
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

        bool fallbackPresent = FallbackMatches() && LauncherMatches();

        if (probe.Presence == GatewayTaskPresence.Missing)
        {
            if (OwnsLegacyFallback())
            {
                return new GatewayPersistenceStatus(
                    GatewayPersistenceState.ActionRequired,
                    GatewayPersistenceLane.StartupFolderFallback,
                    "The Startup-folder recovery uses the previous console launcher.",
                    "Reinstall logon recovery to run it in the background.",
                    RepairCommand);
            }

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
        // Registering the wscript lane while Windows Script Host is disabled
        // makes the task show a blocking dialog at every sign-in. Register and
        // write nothing, leave any previous generation as it is, and let setup
        // complete with a warning; status reports the same condition.
        if (FindDisabledScriptHostSetting() is { } disabledBy)
        {
            return new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.None,
                "Logon recovery was not configured because Windows Script Host is disabled.",
                Changed: false,
                Warning: DescribeDisabledScriptHost(disabledBy));
        }

        bool changed;
        try
        {
            changed = WriteLauncher();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _log(
                $"The gateway launcher '{_options.LauncherPath}' could not be written: " +
                DiagnosticFailure.Describe(exception));
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
            _log(
                $"The logon task '{_identity.Name}' could not be read, so it was " +
                $"left unchanged: {DiagnosticFailure.SingleLine(probe.Detail ?? "no detail was reported.")}");
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
            return CompleteMigration(new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.",
                changed));
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
            GatewayPersistenceStatus verified = await ReadStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            if (verified.State != GatewayPersistenceState.Ready ||
                verified.Lane != GatewayPersistenceLane.TaskScheduler)
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
            // The unchanged task may still reference the CMD launcher. A new
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

        GatewayGeneratedFileRemoval fallback = RemoveFallback();
        GatewayGeneratedFileRemoval launcher = RemoveLauncher();
        GatewayGeneratedFileRemoval activationScript = RemoveActivationScript();
        GatewayGeneratedFileRemoval legacy = RemoveLegacyFiles();
        string? detail = Combine(
            deletion.Detail,
            Combine(fallback.Detail, launcher.Detail, Combine(activationScript.Detail, legacy.Detail)));

        if (!deletion.Succeeded || detail is not null)
        {
            _log($"Logon recovery could not be fully removed: {DiagnosticFailure.SingleLine(detail ?? "no detail was reported.")}");
            return new GatewayPersistenceRemovalResult(
                Succeeded: false,
                fallback.Changed || launcher.Changed || activationScript.Changed || legacy.Changed,
                "Logon recovery could not be fully removed.",
                detail);
        }

        return new GatewayPersistenceRemovalResult(
            Succeeded: true,
            fallback.Changed || launcher.Changed || activationScript.Changed || legacy.Changed,
            "Logon recovery is removed.");
    }

    // Shipped CMD registrations remain recoverable until an explicit install
    // switches the task or fallback. Remove only marked files after that succeeds.
    private GatewayPersistenceInstallResult CompleteMigration(GatewayPersistenceInstallResult result)
    {
        if (result.State != GatewayPersistenceState.Ready)
        {
            return result;
        }

        GatewayGeneratedFileRemoval fallback = result.Lane == GatewayPersistenceLane.TaskScheduler
            ? RemoveFallback()
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

    // Status offers the same command after the value changes: setup is
    // idempotent, and it is the only command that configures recovery. The
    // registry is only read; changing it is the user's or the administrator's
    // decision.
    private static string DescribeDisabledScriptHost(string setting) =>
        $"'{setting}' is 0, and logon recovery runs through Windows Script Host " +
        "(wscript.exe). While it is disabled, setup does not register logon " +
        "recovery, and a registered wscript task shows a blocking dialog at " +
        "sign-in instead of starting the gateway. Set the value to 1 or remove " +
        "it (the machine-wide value needs an administrator and may be set by " +
        $"Group Policy or other management), then run '{RepairCommand}' to " +
        "configure logon recovery. OpenClaw does not change it.";

    private string? FindDisabledScriptHostSetting()
    {
        if (_options.ScriptHostSettings is null)
        {
            return null;
        }

        string? setting = WindowsScriptHostPolicy.FindDisablingSetting(_options.ScriptHostSettings);
        if (setting is not null)
        {
            _log($"Windows Script Host is disabled by '{setting}'.");
        }

        return setting;
    }

    private GatewayGeneratedFileRemoval RemoveLegacyFiles()
    {
        GatewayGeneratedFileRemoval launcher =
            RemoveGeneratedFile(Path.ChangeExtension(LauncherPath, ".cmd"));
        GatewayGeneratedFileRemoval fallback =
            RemoveGeneratedFile(Path.ChangeExtension(FallbackPath, ".cmd"));
        return new GatewayGeneratedFileRemoval(
            launcher.Changed || fallback.Changed,
            Combine(launcher.Detail, fallback.Detail));
    }

    private GatewayTaskSnapshot DesiredSnapshot() =>
        GatewayTaskDefinition.CreateSnapshot(
            _options.UserSid,
            _options.ScriptHostPath,
            LauncherPath,
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
        try
        {
            Directory.CreateDirectory(_options.StartupFolderPath);
            WriteGeneratedFile(
                FallbackPath,
                GatewayLauncherScript.CreateFallback(_options.LauncherPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _log(
                $"The Startup-folder fallback '{FallbackPath}' could not be written: " +
                DiagnosticFailure.Describe(exception));
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
            ActivationScriptPath);
        string activationScript =
            GatewayLauncherScript.CreateActivationScript(_options.PackageFamilyName);

        Directory.CreateDirectory(
            Path.GetDirectoryName(LauncherPath)
            ?? throw new InvalidOperationException(
                $"'{LauncherPath}' has no parent directory."));
        bool launcherChanged = WriteGeneratedFile(LauncherPath, content);
        bool activationChanged = WriteGeneratedFile(ActivationScriptPath, activationScript);
        return launcherChanged || activationChanged;
    }

    private static bool WriteGeneratedFile(string path, string content)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);
            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return false;
            }

            if (!GatewayLauncherScript.LooksGenerated(existing))
            {
                throw new IOException(
                    $"'{path}' exists but was not generated by OpenClaw.");
            }
        }

        // WScript reads Unicode scripts with a UTF-16 BOM; UTF-8 paths would be
        // decoded using the machine's ANSI code page and fail for other alphabets.
        File.WriteAllText(path, content, Path.GetExtension(path) == ".js"
            ? Encoding.Unicode
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private bool LauncherMatches()
    {
        try
        {
            return File.Exists(LauncherPath) &&
                   string.Equals(
                       File.ReadAllText(LauncherPath),
                       GatewayLauncherScript.Create(
                           _options.WorkingDirectory,
                           ActivationScriptPath),
                       StringComparison.Ordinal) &&
                   File.Exists(ActivationScriptPath) &&
                   string.Equals(
                       File.ReadAllText(ActivationScriptPath),
                       GatewayLauncherScript.CreateActivationScript(
                           _options.PackageFamilyName),
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
                   string.Equals(
                       File.ReadAllText(FallbackPath),
                       GatewayLauncherScript.CreateFallback(_options.LauncherPath),
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private GatewayGeneratedFileRemoval RemoveFallback() =>
        RemoveGeneratedFile(FallbackPath);

    private GatewayGeneratedFileRemoval RemoveLauncher() =>
        RemoveGeneratedFile(LauncherPath);

    private GatewayGeneratedFileRemoval RemoveActivationScript() =>
        RemoveGeneratedFile(ActivationScriptPath);

    // The one ownership test: only a file carrying a generator marker is ours.
    // Status and removal share it so they cannot disagree about a legacy file.
    private static bool IsGenerated(string path) =>
        File.Exists(path) &&
        GatewayLauncherScript.LooksGenerated(File.ReadAllText(path));

    // An unreadable legacy file is treated as owned: install reports the same
    // file as a failed cleanup, so status and install stay consistent.
    private bool OwnsLegacyFallback()
    {
        try
        {
            return IsGenerated(Path.ChangeExtension(FallbackPath, ".cmd"));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

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
