using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.StateTransfer;

internal sealed partial class StateTransferCoordinator
{
    public async Task<StateArchiveCommandResult> RecoverAsync(
        RecoverOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!options.DryRun && !options.Yes && _confirm is null)
            {
                throw new SessionException(
                    "Recovering into the current agent requires confirmation. Use `--yes` with JSON or redirected input/output.");
            }
            using var source = new RecoverySource(options.Profile);
            string? destination = options.DryRun ? null : _store.ValidateDestination(options.Output);
            if (destination is not null && StateTransferFiles.Covers(source.ProfilePath, destination))
            {
                throw new SessionException("A recovery archive must be retained outside the original profile.");
            }
            if (!options.DryRun && !options.Yes &&
                !await _confirm!(source.ProfilePath, cancellationToken).ConfigureAwait(false))
            {
                return new StateArchiveCommandResult("recover", 1,
                    Error: "Recovery was cancelled; neither profile was changed.");
            }
            SessionRuntime runtime = _createRuntime();
            using ISessionLockHandle lifecycle = AcquireLock(runtime);
            _ = runtime.RequireSetup();
            SessionRecord record = await runtime.Coordinator.StartRecordedAsync(cancellationToken).ConfigureAwait(false);
            using var operation = new SessionWorkspaceOperation(record, runtime.IsCurrentSessionRecord);
            SessionStateTransferResult current = await InspectAsync(runtime, record, cancellationToken).ConfigureAwait(false);
            if (current.Pending)
            {
                throw new SessionException("Activation is already pending. Run `clawctl restore --rollback --yes` first.");
            }
            if (string.Equals(source.ProfilePath, current.ProfileDirectory, StringComparison.OrdinalIgnoreCase) ||
                StateTransferFiles.Covers(source.ProfilePath, operation.WorkspacePath))
            {
                throw new SessionException("Recovery cannot use the current agent or its shared workspace as the old profile.");
            }
            string snapshot = operation.FilePath("state-source", Guid.NewGuid().ToString("N"), "");
            operation.EnsureDirectory(snapshot);
            bool cleanupAttempted = false;
            StateArchiveCommandResult Finish(StateArchiveCommandResult result)
            {
                cleanupAttempted = true;
                IReadOnlyList<string> warnings = CleanupRecoverySnapshot(operation, snapshot);
                return result.Transfer is { } transfer && warnings.Count != 0
                    ? result with { Transfer = transfer with { Warnings = [.. transfer.Warnings, .. warnings] } }
                    : result;
            }

            try
            {
                _ = await source.CopyAsync(
                    [Path.Combine(source.ProfilePath, ".openclaw")], snapshot, operation,
                    cancellationToken).ConfigureAwait(false);
                for (int pass = 0; pass < 10000; pass++)
                {
                    SessionStateTransferRequest request = CreateRequest(runtime, SessionStateTransferAction.Recover) with
                    {
                        SourceProfile = source.ProfilePath,
                        SourceDirectory = snapshot,
                        SourceSha256 = StateTransferFiles.Digest(snapshot),
                        DryRun = options.DryRun
                    };
                    string archivePath = Path.Combine(operation.WorkspacePath, $"state-archive-{request.RequestId}.tar.gz");
                    try
                    {
                        SessionStateTransferResult recovered = await TransferAsync(
                            runtime, record, operation, request, cancellationToken).ConfigureAwait(false);
                        if (recovered.Phase == "needs-source")
                        {
                            int copied = await source.CopyAsync(
                                recovered.RequiredSources, snapshot, operation, cancellationToken).ConfigureAwait(false);
                            if (copied == 0)
                            {
                                throw new SessionException("A required recovery dependency could not be resolved inside the source profile.");
                            }
                            continue;
                        }
                        source.EnsureUnchanged();
                        if (options.DryRun)
                        {
                            return Finish(new StateArchiveCommandResult("recover", 0, recovered with { Phase = "preview" }));
                        }
                        StateTransferArchive archive = recovered.Archive ??
                            throw new SessionException("The guest did not produce a verified normalized recovery archive.");
                        string retained;
                        using (FileStream input = operation.OpenRead(archive.Path, protectContents: true))
                        {
                            retained = await _store.PublishAsync(
                                input, archive, destination, protection: false, cancellationToken).ConfigureAwait(false);
                        }
                        StateArchiveCommandResult restored = await RestoreUnderLockAsync(
                            "recover", runtime, record, operation, retained, dryRun: false,
                            cancellationToken).ConfigureAwait(false);
                        return Finish(restored with
                        {
                            Transfer = (restored.Transfer ?? recovered) with
                            {
                                Archive = archive with { Path = retained },
                                Warnings = [.. (restored.Transfer?.Warnings ?? recovered.Warnings),
                                    "The original profile was preserved. OS-bound credentials may require reauthentication or channel relinking."]
                            }
                        });
                    }
                    finally
                    {
                        operation.Delete(archivePath);
                    }
                }
                throw new SessionException("Recovery configuration dependencies exceeded the supported discovery limit.");
            }
            finally
            {
                if (!cleanupAttempted)
                {
                    _ = CleanupRecoverySnapshot(operation, snapshot);
                }
            }
        }
        catch (Exception exception) when (IsOperationalFailure(exception) || exception is OperationCanceledException)
        {
            return Failure("recover", exception);
        }
    }

    private IReadOnlyList<string> CleanupRecoverySnapshot(SessionWorkspaceOperation operation, string snapshot)
    {
        try
        {
            operation.EnsureCurrent();
            StateTransferFiles.Delete(snapshot);
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SessionException)
        {
            _log($"Recovery snapshot cleanup failed: {DiagnosticFailure.Describe(exception)}");
            return [$"Temporary recovery snapshot could not be removed at '{snapshot}'. " +
                "It may contain credentials. Collect diagnostics before cleaning this directory."];
        }
    }
}
