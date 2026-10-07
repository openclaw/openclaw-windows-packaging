using System.Security.Cryptography;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.StateTransfer;

internal sealed partial class StateTransferCoordinator
{
    public async Task<StateArchiveCommandResult> RestoreAsync(
        RestoreOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!options.DryRun && !options.Yes && _confirm is null)
            {
                throw new SessionException(
                    "Replacing agent state requires confirmation. Use `--yes` with JSON or redirected input/output.");
            }
            string? selected = options.Rollback ? null : _store.Select(options.Path);
            if (!options.DryRun && !options.Yes &&
                !await _confirm!(selected ?? "the interrupted activation", cancellationToken).ConfigureAwait(false))
            {
                return new StateArchiveCommandResult("restore", 1, Error: "Restore was cancelled; agent state was not changed.");
            }
            SessionRuntime runtime = _createRuntime();
            using ISessionLockHandle lifecycle = AcquireLock(runtime);
            _ = runtime.RequireSetup();
            SessionRecord record = await runtime.Coordinator
                .StartRecordedAsync(cancellationToken).ConfigureAwait(false);
            using var operation = new SessionWorkspaceOperation(record, runtime.IsCurrentSessionRecord);
            if (options.Rollback)
            {
                GatewayStopResult stopped = await _stopGateway(runtime, cancellationToken).ConfigureAwait(false);
                RequireStopped(stopped);
                SessionStateTransferResult rolledBack = await TransferAsync(
                    runtime, record, operation,
                    new SessionStateTransferRequest
                    {
                        RequestId = Guid.NewGuid().ToString("N"),
                        Action = SessionStateTransferAction.Rollback
                    },
                    cancellationToken).ConfigureAwait(false);
                return new StateArchiveCommandResult("restore", 0, rolledBack with { GatewayStopped = true });
            }
            return await RestoreUnderLockAsync(
                "restore", runtime, record, operation, selected!, options.DryRun,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationalFailure(exception) || exception is OperationCanceledException)
        {
            return Failure("restore", exception);
        }
    }

    private async Task<StateArchiveCommandResult> RestoreUnderLockAsync(
        string command,
        SessionRuntime runtime,
        SessionRecord record,
        SessionWorkspaceOperation operation,
        string selected,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        string staged = operation.FilePath("state-input", Guid.NewGuid().ToString("N"), ".tar.gz");
        SessionStateTransferResult? prepared = null;
        StateTransferArchive? protection = null;
        bool activationStarted = false;
        bool gatewayStopped = false;
        try
        {
            StateTransferArchive input = await StageArchiveAsync(
                selected, staged, operation, cancellationToken).ConfigureAwait(false);
            string transaction = Guid.NewGuid().ToString("N");
            SessionStateTransferRequest request = CreateRequest(
                runtime, dryRun ? SessionStateTransferAction.Preview : SessionStateTransferAction.Prepare) with
            {
                ArchivePath = staged,
                ArchiveSha256 = input.Sha256,
                TransactionId = transaction,
                DryRun = dryRun
            };
            prepared = await TransferAsync(
                runtime, record, operation, request, cancellationToken).ConfigureAwait(false);
            input = input with { Verified = true };
            if (dryRun)
            {
                return new StateArchiveCommandResult(command, 0,
                    prepared with { Archive = input, Phase = "preview" });
            }
            if (prepared.TransactionId != transaction || !prepared.Pending || prepared.Phase != "prepared")
            {
                throw new SessionException("The guest did not record the expected prepared activation.");
            }
            GatewayStopResult stopped = await _stopGateway(runtime, cancellationToken).ConfigureAwait(false);
            RequireStopped(stopped);
            gatewayStopped = true;
            SessionStateTransferRequest captureRequest = CreateRequest(runtime, SessionStateTransferAction.Capture) with
            {
                TransactionId = transaction
            };
            string capturePath = Path.Combine(operation.WorkspacePath, $"state-archive-{captureRequest.RequestId}.tar.gz");
            try
            {
                SessionStateTransferResult captured = await TransferAsync(
                    runtime, record, operation, captureRequest, cancellationToken).ConfigureAwait(false);
                if (captured.Archive is { } archive)
                {
                    using FileStream source = operation.OpenRead(archive.Path, protectContents: true);
                    string retained = await _store.PublishAsync(
                        source, archive, null, protection: true, cancellationToken).ConfigureAwait(false);
                    protection = archive with { Path = retained };
                }
                else if (captured.HasState)
                {
                    throw new SessionException("Meaningful current state was not protected by a verified archive.");
                }
            }
            finally
            {
                operation.Delete(capturePath);
            }
            activationStarted = true;
            SessionStateTransferResult activated = await TransferAsync(
                runtime, record, operation, CreateRequest(runtime, SessionStateTransferAction.Activate) with
                {
                    TransactionId = transaction,
                    ProtectionArchive = protection?.Path,
                    ProtectionSha256 = protection?.Sha256
                },
                cancellationToken).ConfigureAwait(false);
            if (activated.Pending || activated.Phase != "completed")
            {
                throw new SessionException("The guest did not confirm completed state activation.");
            }
            return new StateArchiveCommandResult(command, 0, activated with
            {
                Archive = input,
                Assets = prepared.Assets,
                Mappings = prepared.Mappings,
                ProtectionArchive = protection?.Path,
                GatewayStopped = true,
                Warnings = [.. prepared.Warnings, .. activated.Warnings,
                    protection is null ? "The target had no prior OpenClaw state to protect." :
                        "A verified pre-restore archive was retained."]
            });
        }
        catch (Exception exception) when (IsOperationalFailure(exception) || exception is OperationCanceledException)
        {
            string? cleanupError = null;
            if (prepared?.Pending == true && !activationStarted)
            {
                try
                {
                    _ = await TransferAsync(
                        runtime, record, operation,
                        new SessionStateTransferRequest
                        {
                            RequestId = Guid.NewGuid().ToString("N"),
                            Action = SessionStateTransferAction.Rollback
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanup) when (IsOperationalFailure(cleanup))
                {
                    cleanupError = cleanup.Message;
                    _log($"Prepared restore cleanup failed: {DiagnosticFailure.Describe(cleanup)}");
                }
            }
            string message = exception is OperationCanceledException ? "State transfer was cancelled." : exception.Message;
            if (activationStarted || cleanupError is not null)
            {
                message += " Activation may be pending. Run `clawctl status`, then `clawctl restore --rollback --yes`.";
            }
            if (cleanupError is not null)
            {
                message += " " + cleanupError;
            }
            _log($"State activation failed: {DiagnosticFailure.Describe(exception)}");
            return new StateArchiveCommandResult(command, 1,
                prepared is null ? null : prepared with
                {
                    ProtectionArchive = protection?.Path,
                    Pending = activationStarted || cleanupError is not null,
                    Phase = activationStarted ? "interrupted" : "not-activated",
                    GatewayStopped = gatewayStopped
                }, Error: message);
        }
        finally
        {
            operation.Delete(staged);
        }
    }

    internal static async Task<SessionStateTransferResult> InspectAsync(
        SessionRuntime runtime,
        SessionRecord record,
        CancellationToken cancellationToken)
    {
        using var operation = new SessionWorkspaceOperation(record, runtime.IsCurrentSessionRecord);
        return await TransferAsync(
            runtime, record, operation,
            new SessionStateTransferRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Action = SessionStateTransferAction.Inspect
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SessionStateTransferResult?> InspectAvailableAsync(
        SessionRuntime runtime,
        SessionStatus status,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (status.Availability != SessionAvailability.Running || status.Record is not { } record)
        {
            return null;
        }
        try
        {
            return await InspectAsync(runtime, record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SessionException or SessionLaunchException or
            Mxc.MxcException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log($"State activation inspection failed: {DiagnosticFailure.Describe(exception)}");
            return new SessionStateTransferResult
            {
                Phase = "unavailable",
                Pending = true,
                Error = "Activation status could not be established. " + exception.Message
            };
        }
    }

    private static Task<SessionStateTransferResult> TransferAsync(
        SessionRuntime runtime,
        SessionRecord record,
        SessionWorkspaceOperation operation,
        SessionStateTransferRequest request,
        CancellationToken cancellationToken) =>
        runtime.Executor.TransferStateAsync(
            record, runtime.RequireStagedHelper(record), operation, request, cancellationToken);

    private static async Task<StateTransferArchive> StageArchiveAsync(
        string source,
        string destination,
        SessionWorkspaceOperation operation,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(source);
        using FileStream input = TrustedPath.OpenRead(
            Path.GetPathRoot(fullPath)!, fullPath, protectContents: true);
        using (Stream output = operation.CreateNew(destination))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        if (input.Length == 0)
        {
            throw new SessionException("The selected archive is empty.");
        }
        input.Position = 0;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        return new StateTransferArchive(fullPath, input.Length, hash, Verified: false);
    }

    private StateArchiveCommandResult Failure(string command, Exception exception)
    {
        _log($"{command} failed: {DiagnosticFailure.Describe(exception)}");
        return new StateArchiveCommandResult(command, 1,
            Error: exception is OperationCanceledException ? "State transfer was cancelled." : exception.Message);
    }

    private static void RequireStopped(GatewayStopResult result)
    {
        if (!result.Succeeded)
        {
            throw new SessionException(result.Detail ?? result.Message);
        }
    }
}
