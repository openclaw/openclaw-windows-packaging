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
    public const string RepairCommand = "clawctl gateway-service install";

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
        $"{GatewayTaskIdentity.DisplayName} {_options.PackageFamilyName}.cmd");

    private string LauncherPath => ResolveOwnedFilePath(
        _options.WorkingDirectory,
        _options.LauncherPath);

    private string ActivationScriptPath => ResolveOwnedFilePath(
        _options.WorkingDirectory,
        Path.ChangeExtension(LauncherPath, ".ps1"));

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

        bool fallbackPresent = FallbackMatches() && LauncherMatches();

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
            _ = RemoveFallback();
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
            _ = RemoveFallback();
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

        CleanupResult fallback = RemoveFallback();
        CleanupResult launcher = RemoveLauncher();
        CleanupResult activationScript = RemoveActivationScript();
        bool changed =
            fallback == CleanupResult.Removed ||
            launcher == CleanupResult.Removed ||
            activationScript == CleanupResult.Removed;

        if (!deletion.Succeeded ||
            fallback == CleanupResult.Failed ||
            launcher == CleanupResult.Failed ||
            activationScript == CleanupResult.Failed)
        {
            return new GatewayPersistenceRemovalResult(
                Succeeded: false,
                changed,
                "Logon recovery could not be fully removed.",
                Combine(
                    deletion.Succeeded ? null : deletion.Detail,
                    Combine(
                        fallback == CleanupResult.Failed
                            ? $"'{FallbackPath}' could not be removed."
                            : null,
                        launcher == CleanupResult.Failed
                            ? $"'{_options.LauncherPath}' could not be removed."
                            : null,
                        activationScript == CleanupResult.Failed
                            ? $"'{ActivationScriptPath}' could not be removed."
                            : null)));
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
        else if (!MatchesUserSid(actual.LogonTriggerUserId, desired.LogonTriggerUserId))
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
            WriteGeneratedFile(
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

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    private CleanupResult RemoveFallback() =>
        RemoveGeneratedFile(FallbackPath);

    private CleanupResult RemoveLauncher() =>
        RemoveGeneratedFile(LauncherPath);

    private CleanupResult RemoveActivationScript() =>
        RemoveGeneratedFile(ActivationScriptPath);

    private static CleanupResult RemoveGeneratedFile(string path)
    {
        // Only a file this installation generated is deleted. A same-named file
        // someone else placed there is left alone.
        try
        {
            if (!File.Exists(path) ||
                !GatewayLauncherScript.LooksGenerated(File.ReadAllText(path)))
            {
                return CleanupResult.Absent;
            }

            File.Delete(path);
            return CleanupResult.Removed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return CleanupResult.Failed;
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

    private enum CleanupResult
    {
        Absent,
        Removed,
        Failed,
    }
}
