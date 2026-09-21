using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Session;

/// <summary>What an <see cref="SetupOrchestrator.EnsureAsync"/> call actually did.</summary>
internal enum SetupEnsureOutcome
{
    /// <summary>
    /// Nothing was provisioned. Either this installation is already set up, or
    /// its recorded state is degraded in a way that only explicit
    /// <c>clawctl setup</c> may resolve. The caller's own validation reports
    /// which of those it is.
    /// </summary>
    Skipped,

    /// <summary>This call provisioned the installation.</summary>
    Provisioned
}

/// <summary>
/// The single owner of the setup route.
/// </summary>
/// <remarks>
/// <para>
/// Both entry points reach setup through this type: <c>clawctl setup</c> via
/// <see cref="RunAsync"/>, and an <c>openclaw</c> launch on a clean machine via
/// <see cref="EnsureAsync"/>. They share <see cref="RunCoreAsync"/> so there is
/// one provisioning sequence rather than two that can drift.
/// </para>
/// <para>
/// The lifecycle lock is not re-entrant: every
/// <see cref="ISessionLock.TryAcquire"/> takes ownership on a fresh dedicated
/// thread, so a second acquire from this process would block against itself
/// until the timeout. That is why <see cref="RunCoreAsync"/> takes
/// <c>lockAlreadyHeld</c>, and why <see cref="EnsureAsync"/> releases the lock
/// before returning to a caller that is about to start the session.
/// </para>
/// </remarks>
internal static class SetupOrchestrator
{
    /// <summary>
    /// Provisions this installation when, and only when, it has never been set
    /// up.
    /// </summary>
    /// <remarks>
    /// Only an absent marker is provisioned automatically. An unreadable,
    /// incomplete, foreign, newer-schema, preparing, or tearing-down marker is
    /// left exactly as it is so that its explicit recovery message survives:
    /// silently reprovisioning would destroy state a user may be mid-recovering.
    /// </remarks>
    public static async Task<SetupEnsureOutcome> EnsureAsync(
        HostOptions options,
        SessionRuntime runtime,
        IInstallationLifecycle lifecycle,
        string applicationDirectory,
        Action<string> log,
        IProgress<ClawCtlProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(progress);

        if (!IsAbsent(runtime.SetupState.Read(runtime.ApplicationId)))
        {
            return SetupEnsureOutcome.Skipped;
        }

        SetupCommandResult result;
        using (runtime.AcquireLifecycleLock())
        {
            // Re-read under the lock. Two first launches racing on a clean
            // machine must produce one provision, not two.
            if (!IsAbsent(runtime.SetupState.Read(runtime.ApplicationId)))
            {
                log("Setup completed elsewhere while this launch waited for the lifecycle lock.");
                return SetupEnsureOutcome.Skipped;
            }

            log("No setup record exists; provisioning this installation before launching OpenClaw.");
            result = await RunCoreAsync(
                runtime,
                options,
                lifecycle,
                applicationDirectory,
                log,
                lockAlreadyHeld: true,
                warning: null,
                localStateCleared: false,
                fresh: false,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        if (result.ExitCode != 0)
        {
            throw new SessionException(
                $"Automatic setup did not complete: {DescribeFailure(result)} " +
                "Run `clawctl setup` to finish preparing OpenClaw.");
        }

        return SetupEnsureOutcome.Provisioned;
    }

    /// <summary>
    /// Whether this installation has never been set up, and so may be
    /// provisioned automatically by a launch.
    /// </summary>
    /// <remarks>
    /// Callers use this to decide whether to narrate before calling
    /// <see cref="EnsureAsync"/>. It is deliberately not authoritative:
    /// <see cref="EnsureAsync"/> re-reads the same state under the lifecycle
    /// lock, because another process may provision in between.
    /// </remarks>
    public static bool NeedsProvisioning(SessionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return IsAbsent(runtime.SetupState.Read(runtime.ApplicationId));
    }

    private static bool IsAbsent(SetupStateResult state) =>
        state.Record is null && state.Fault == SetupStateFault.Missing;

    private static string DescribeFailure(SetupCommandResult result) =>
        result.Error
        ?? result.Recovery?.Detail
        ?? result.Recovery?.Message
        ?? "the reason was not reported.";

    public static async Task<SetupCommandResult> RunAsync(
        SetupOptions setupOptions,
        HostOptions options,
        Func<SessionRuntime> getSessionRuntime,
        IInstallationLifecycle lifecycle,
        Action<string> log,
        IProgress<ClawCtlProgress> progress,
        CancellationToken cancellationToken)
    {
        string applicationDirectory = options.RequirePackagedApplicationDirectory();
        log("Confirmed the packaged OpenClaw application is present.");
        // Throws with the reason and its remediation when this machine cannot
        // host a session. There is no session-free setup to fall back to, so
        // nothing is reported as ready before this succeeds.
        await lifecycle.EnsureSessionSupportedAsync(cancellationToken)
            .ConfigureAwait(false);

        FreshSetupWarning? warning = null;
        bool localStateCleared = false;
        try
        {
            SessionRuntime runtime = getSessionRuntime();
            if (setupOptions.Fresh)
            {
                progress.Report(new ClawCtlProgress("Inspecting the existing installation."));
                // Resolve every required package input before removing state.
                _ = lifecycle.ValidatePackageRuntime(options, runtime);

                using ISessionLockHandle handle = lifecycle.AcquireLifecycleLock(runtime);
                FreshDiagnosticReport report = WriteFreshDiagnosticReport(runtime, log);
                TeardownResult teardownResult;
                try
                {
                    progress.Report(new ClawCtlProgress("Removing existing OpenClaw resources."));
                    teardownResult = await lifecycle.TeardownAsync(
                        options, runtime, log, lockAlreadyHeld: true, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    setupOptions.Force &&
                    exception is SessionException or MxcException)
                {
                    teardownResult = new TeardownResult(
                        false,
                        "Teardown failed before external cleanup could be confirmed.",
                        exception.Message);
                }
                if (!teardownResult.Succeeded)
                {
                    if (!setupOptions.Force)
                    {
                        return new SetupCommandResult(
                            1,
                            applicationDirectory,
                            null,
                            null,
                            false,
                            Error: teardownResult.Detail ?? teardownResult.Message,
                            Fresh: true);
                    }

                    warning = new FreshSetupWarning(
                        teardownResult.Message,
                        teardownResult.Detail);
                }

                IInstallationStateCleaner cleaner = lifecycle.CreateStateCleaner(runtime);
                try
                {
                    progress.Report(new ClawCtlProgress("Clearing package-local state."));
                    cleaner.Clear();
                    localStateCleared = true;
                }
                catch (Exception cleanupException)
                {
                    try
                    {
                        RestoreFreshDiagnosticReport(report, log);
                    }
                    catch (Exception restoreException)
                    {
                        throw new SessionException(
                            "Fresh setup local cleanup failed and the pre-reset " +
                            $"diagnostic report could not be restored. Cleanup: {cleanupException.Message} " +
                            $"Report: {restoreException.Message}",
                            new AggregateException(cleanupException, restoreException));
                    }

                    throw;
                }

                RestoreFreshDiagnosticReport(report, log);
                log("Fresh setup cleared package-owned local state.");
                if (!teardownResult.Succeeded)
                {
                    const string residualWarning =
                        "WARNING: Forced fresh setup did not prove a pristine machine because owned external cleanup remains unresolved. " +
                        "Review the pre-reset report for residual sandbox or gateway identifiers. " +
                        "A later setup reset cannot remove resources whose ownership record was cleared; " +
                        "remove them through the backend's administrative cleanup path before treating this machine as pristine.";
                    log(residualWarning);
                }

                return await RunCoreAsync(
                    runtime,
                    options,
                    lifecycle,
                    applicationDirectory,
                    log,
                    lockAlreadyHeld: true,
                    warning,
                    localStateCleared,
                    fresh: true,
                    progress,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RunCoreAsync(
                runtime,
                options,
                lifecycle,
                applicationDirectory,
                log,
                lockAlreadyHeld: false,
                warning,
                localStateCleared,
                fresh: false,
                progress,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SessionException exception)
        {
            log($"Isolated session setup is unavailable: {exception.Message}");
            return new SetupCommandResult(
                1,
                applicationDirectory,
                null,
                null,
                false,
                warning,
                Error: exception.Message,
                Fresh: setupOptions.Fresh,
                LocalStateCleared: localStateCleared);
        }
        catch (IOException exception)
        {
            log($"Fresh setup local cleanup failed: {exception.Message}");
            return new SetupCommandResult(
                1,
                applicationDirectory,
                null,
                null,
                false,
                warning,
                Error: exception.Message,
                Fresh: setupOptions.Fresh,
                LocalStateCleared: localStateCleared);
        }
        catch (UnauthorizedAccessException exception)
        {
            log($"Fresh setup local cleanup was denied: {exception.Message}");
            return new SetupCommandResult(
                1,
                applicationDirectory,
                null,
                null,
                false,
                warning,
                Error: exception.Message,
                Fresh: setupOptions.Fresh,
                LocalStateCleared: localStateCleared);
        }
        catch (OperationCanceledException)
        {
            string retryCommand = setupOptions.Fresh
                ? "clawctl setup --fresh"
                : "clawctl setup";
            log($"Setup was cancelled before it completed. Retry `{retryCommand}`.");
            return new SetupCommandResult(
                1,
                applicationDirectory,
                null,
                null,
                false,
                warning,
                Error:
                    "Setup was cancelled and may be incomplete. " +
                    $"Run `{retryCommand}` to retry.",
                Fresh: setupOptions.Fresh,
                LocalStateCleared: localStateCleared);
        }
    }

    internal static async Task<SetupCommandResult> RunCoreAsync(
        SessionRuntime runtime,
        HostOptions options,
        IInstallationLifecycle lifecycle,
        string applicationDirectory,
        Action<string> log,
        bool lockAlreadyHeld,
        FreshSetupWarning? warning,
        bool localStateCleared,
        bool fresh,
        IProgress<ClawCtlProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new ClawCtlProgress("Preparing the isolated session."));
        using ISessionLockHandle? handle = lockAlreadyHeld
            ? null
            : runtime.AcquireLifecycleLock();
        runtime.SetupState.Write(new SetupRecord
        {
            ApplicationId = runtime.ApplicationId,
            Phase = SetupPhase.Preparing
        });
        SessionStartResult session = await runtime.Coordinator
            .EnsureStartedWithResultAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<string> supersededSandboxIds = (session.Record.SupersededSandboxIds ?? [])
            .Append(session.Record.SupersededSandboxId)
            .Append(session.SupersededRecord?.SandboxId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .Where(id => !string.Equals(
                id,
                session.Record.SandboxId,
                StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
        foreach (string supersededSandboxId in supersededSandboxIds)
        {
            if (runtime.GatewayState.ClearForSupersededSession(supersededSandboxId))
            {
                log($"Removed the gateway record for superseded session '{supersededSandboxId}'.");
            }
        }

        SessionRecord record = session.Record;
        string helperPath = runtime.StageHelper(record);
        progress.Report(new ClawCtlProgress(
            "Installing Node.js in the isolated session."));
        SessionRuntimeInstallResult agentRuntime = await runtime.Executor.InstallRuntimeAsync(
            record,
            helperPath,
            options.RequirePackagedNodeArchivePath(),
            applicationDirectory,
            cancellationToken).ConfigureAwait(false);
        progress.Report(new ClawCtlProgress("Enabling gateway startup at sign-in."));
        GatewayPersistenceInstallResult recovery = await lifecycle
            .InstallRecoveryAsync(log, cancellationToken).ConfigureAwait(false);

        if (recovery.State != GatewayPersistenceState.Ready)
        {
            return new SetupCommandResult(
                1,
                applicationDirectory,
                $"{agentRuntime.Version}",
                recovery,
                false,
                warning,
                SandboxId: record.SandboxId,
                Fresh: fresh,
                RuntimeLocation: SetupRuntimeLocation.IsolatedSession,
                LocalStateCleared: localStateCleared);
        }

        progress.Report(new ClawCtlProgress("Finalizing setup."));
        runtime.CompleteSetup(record, agentRuntime, startupEnabled: true);
        return new SetupCommandResult(
            0,
            applicationDirectory,
            $"{agentRuntime.Version}",
            recovery,
            true,
            warning,
            SandboxId: record.SandboxId,
            Fresh: fresh,
            RuntimeLocation: SetupRuntimeLocation.IsolatedSession,
            LocalStateCleared: localStateCleared);
    }

    private static FreshDiagnosticReport WriteFreshDiagnosticReport(
        SessionRuntime runtime,
        Action<string> log)
    {
        SessionStatus session = runtime.Coordinator.GetRecordedStatus();
        GatewayStateResult gateway = runtime.GatewayState.Read();
        string path = runtime.Paths.PreResetReportPath;
        string directory = Path.GetDirectoryName(path)
            ?? throw new SessionException(
                "The pre-reset diagnostic report path has no parent directory.");
        Directory.CreateDirectory(directory);
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path))
            : [];
        if (lines.Count > 0)
        {
            lines.Add(string.Empty);
        }

        lines.AddRange(
        [
            "--- pre-reset snapshot ---",
            $"timestampUtc={DateTimeOffset.UtcNow:O}",
            $"applicationId={runtime.ApplicationId}",
            "report=pre-reset diagnostic metadata; credentials and local file contents are excluded"
        ]);
        if (session.Record is not null)
        {
            lines.Add($"sessionSandboxId={session.Record.SandboxId}");
            lines.Add($"sessionAgentUserName={session.Record.AgentUserName ?? string.Empty}");
            lines.Add($"sessionAgentUserSid={session.Record.AgentUserSid ?? string.Empty}");
        }
        else
        {
            lines.Add($"sessionRecordFault={session.Fault?.ToString() ?? "none"}");
        }

        if (gateway.Record is not null)
        {
            lines.Add($"gatewaySandboxId={gateway.Record.SandboxId}");
            lines.Add($"gatewayProcessId={gateway.Record.ProcessId}");
            lines.Add($"gatewayLaunchPending={gateway.Record.LaunchPending}");
        }
        else
        {
            lines.Add($"gatewayRecordFault={gateway.Fault?.ToString() ?? "none"}");
        }
        lines.Add("--- end pre-reset snapshot ---");

        File.WriteAllLines(path, lines);
        log($"Captured redacted pre-reset diagnostic report at {path}.");
        return new FreshDiagnosticReport(path, lines);
    }

    private static void RestoreFreshDiagnosticReport(
        FreshDiagnosticReport report,
        Action<string> log)
    {
        string directory = Path.GetDirectoryName(report.Path)
            ?? throw new SessionException(
                "The pre-reset diagnostic report path has no parent directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(report.Path, report.Lines);
        log($"Preserved the redacted pre-reset diagnostic report at {report.Path}.");
    }

    private sealed record FreshDiagnosticReport(
        string Path,
        IReadOnlyList<string> Lines);
}
