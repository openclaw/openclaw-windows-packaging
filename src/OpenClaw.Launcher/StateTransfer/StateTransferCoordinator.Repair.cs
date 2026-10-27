using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.StateTransfer;

internal sealed partial class StateTransferCoordinator
{
    public async Task<StateArchiveCommandResult> RepairAsync(bool confirmed, CancellationToken cancellationToken)
    {
        const string command = "gateway-service repair";
        SessionStateTransferResult? backup = null;
        bool stopped = false;
        try
        {
            if (!confirmed)
            {
                throw new SessionException(
                    "Repair stops the gateway, retains a verified backup, and runs OpenClaw Doctor with --fix. " +
                    "Run `clawctl gateway-service repair --yes` to confirm. The gateway remains stopped.");
            }
            SessionRuntime runtime = _createRuntime();
            using ISessionLockHandle lifecycle = AcquireLock(runtime);
            _ = runtime.RequireSetup();
            SessionRecord record = await runtime.Coordinator.StartRecordedAsync(cancellationToken).ConfigureAwait(false);
            SessionStateTransferResult current = await InspectAsync(runtime, record, cancellationToken).ConfigureAwait(false);
            if (current.Pending)
            {
                throw new SessionException("State activation is pending. Run `clawctl restore --rollback --yes` before repair.");
            }
            RequireStopped(await _stopGateway(runtime, cancellationToken).ConfigureAwait(false));
            stopped = true;
            StateArchiveCommandResult captured = await CaptureBackupUnderLockAsync(
                runtime, record, destination: null, dryRun: false, cancellationToken).ConfigureAwait(false);
            backup = captured.Transfer ?? throw new SessionException("Repair requires a verified backup archive.");
            if (backup.Archive is null)
            {
                throw new SessionException("Repair requires a verified backup archive.");
            }

            // The package owns custody and process lifecycle. Doctor owns all
            // migrations and repairs, using the ordinary external-service environment.
            SessionStateTransferRequest inputs = CreateRequest(runtime, SessionStateTransferAction.Inspect);
            _ = await runtime.Executor.ExecuteCaptureAsync(
                record,
                new SessionExecutionRequest(
                    runtime.RequireStagedHelper(record), inputs.NodePath!, inputs.ApplicationDirectory!,
                    ["doctor", "--fix", "--non-interactive", "--no-workspace-suggestions"],
                    record.WorkspacePath!)
                {
                    AdditionalEnvironment = inputs.Environment,
                    NativeRootPath = inputs.NativeRootPath,
                    NodeArgumentsPrefix = inputs.NativePreloadPath is null ? null :
                        OpenClawRuntimeEnvironment.BuildNativeRedirectNodeArguments(inputs.NativePreloadPath)
                },
                cancellationToken).ConfigureAwait(false);
            return new StateArchiveCommandResult(command, 0, backup with
            {
                Phase = "doctor-completed",
                GatewayStopped = true,
                Warnings = [.. backup.Warnings,
                    "Doctor completed. The gateway remains stopped. Run `clawctl gateway-service start` and verify your model connection."]
            });
        }
        catch (Exception exception) when (IsOperationalFailure(exception) || exception is OperationCanceledException)
        {
            _log($"Gateway repair failed: {DiagnosticFailure.Describe(exception)}");
            return new StateArchiveCommandResult(command, 1,
                backup is null ? null : backup with { GatewayStopped = stopped, Phase = "repair-failed" },
                Error: (exception is OperationCanceledException ? "Repair was cancelled." : exception.Message) +
                    (stopped ? " The gateway remains stopped." : "") +
                    (backup?.Archive is not null ? " The retained backup is available with `clawctl backup --list`." : " Doctor did not run."));
        }
    }
}
