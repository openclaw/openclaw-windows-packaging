using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Session;

internal sealed partial class SessionExecutor
{
    public async Task<SessionStateTransferResult> TransferStateAsync(
        SessionRecord record,
        string helperPath,
        SessionWorkspaceOperation operation,
        SessionStateTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        string requestId = request.RequestId ?? _createRequestId();
        string requestPath = operation.FilePath("state-transfer", requestId);
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        try
        {
            await operation.WriteTextNewAsync(
                requestPath,
                SessionStateTransferProtocol.SerializeRequest(request with
                {
                    RequestId = requestId,
                    WorkspaceDirectory = operation.WorkspacePath
                }),
                cancellationToken).ConfigureAwait(false);
            Mxc.MxcExecutionResult execution = await ExecuteTimedAsync(
                record.ToSandboxIdOrThrow(),
                BuildGuestCommandLine(helperPath, requestPath, "--state-transfer"),
                "State transfer",
                cancellationToken).ConfigureAwait(false);
            string text;
            try
            {
                text = await operation.ReadTextAsync(resultPath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new SessionException(
                    "The isolated session did not report state transfer " +
                    DescribeMissingResult(execution), exception);
            }
            SessionStateTransferResult result =
                SessionStateTransferProtocol.ReadResult(text, requestId);
            if (result.Error is { Length: > 0 } error)
            {
                throw new SessionException(error);
            }
            if (execution.ExitCode != 0)
            {
                throw new SessionException("The guest state operation did not exit successfully.");
            }
            operation.EnsureCurrent();
            return result;
        }
        finally
        {
            operation.Delete(requestPath);
            operation.Delete(resultPath);
        }
    }
}
