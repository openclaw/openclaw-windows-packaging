using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Session;

internal sealed partial class SessionExecutor
{
    /// <summary>
    /// Asks the guest how the recorded native stage compares with the
    /// installed application, without staging or launching anything.
    /// </summary>
    public async Task<SessionNativeStageReport> InspectNativeStageAsync(
        SessionRecord record,
        string helperPath,
        SessionNativeStageReference stage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentNullException.ThrowIfNull(stage);

        string requestId = _createRequestId();
        using var operation = new SessionWorkspaceOperation(record, _isCurrentRecord);
        string requestPath = operation.FilePath("native-stage", requestId);
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        try
        {
            await operation.WriteTextNewAsync(
                requestPath,
                SessionNativeStageProtocol.SerializeRequest(new SessionNativeStageInspectRequest
                {
                    RequestId = requestId,
                    ApplicationDirectory = stage.ApplicationDirectory,
                    RootPath = stage.RootPath
                }),
                cancellationToken).ConfigureAwait(false);

            Mxc.MxcExecutionResult execution = await ExecuteTimedAsync(
                record.ToSandboxIdOrThrow(),
                BuildGuestCommandLine(helperPath, requestPath, "--inspect-native"),
                "Native stage inspection",
                cancellationToken).ConfigureAwait(false);
            operation.EnsureCurrent();
            string text;
            try
            {
                text = await operation.ReadTextAsync(resultPath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new SessionException(
                    "The isolated session did not report native stage inspection " +
                    DescribeMissingResult(execution),
                    exception);
            }

            try
            {
                return SessionNativeStageProtocol.ReadReport(text, requestId);
            }
            catch (SessionLaunchException exception)
            {
                throw new SessionException(
                    $"The isolated session reported an invalid native stage inspection: {exception.Message}",
                    exception);
            }
        }
        finally
        {
            operation.Delete(requestPath);
            operation.Delete(resultPath);
        }
    }
}
