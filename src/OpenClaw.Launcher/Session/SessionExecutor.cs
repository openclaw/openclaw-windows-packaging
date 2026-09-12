using OpenClaw.Launcher.Mxc;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// What to run inside the session.
/// </summary>
internal sealed record SessionExecutionRequest(
    string HelperPath,
    string NodePath,
    string ApplicationDirectory,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

/// <summary>
/// Runs OpenClaw inside the owned session.
/// </summary>
/// <remarks>
/// The backend's execution API takes a command line, not an argument vector,
/// and the pinned runtime flattens it through <c>cmd.exe</c>. Only the guest
/// helper's own path and its request file appear there; the user's arguments
/// travel as JSON in the shared workspace and are never exposed to that
/// flattening.
/// </remarks>
internal sealed class SessionExecutor
{
    private readonly IMxcSessionClient _backend;
    private readonly Action<string> _log;

    public SessionExecutor(IMxcSessionClient backend, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(log);
        _backend = backend;
        _log = log;
    }

    public async Task<int> ExecuteAsync(
        SessionRecord record,
        SessionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(record.WorkspacePath))
        {
            throw new SessionException(
                "The recorded session has no shared workspace, so a launch " +
                "request cannot be delivered to it.");
        }

        string requestId = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(
            record.WorkspacePath,
            $"launch-{requestId}.json");
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);

        var launchRequest = new SessionLaunchRequest
        {
            RequestId = requestId,
            Executable = request.NodePath,
            Arguments = BuildNodeArguments(request),
            WorkingDirectory = request.WorkingDirectory,
            Environment = OpenClawRuntimeEnvironment.Build(),
        };

        try
        {
            await File.WriteAllTextAsync(
                requestPath,
                SessionLaunchProtocol.SerializeRequest(launchRequest),
                cancellationToken).ConfigureAwait(false);

            string commandLine = BuildGuestCommandLine(request.HelperPath, requestPath);
            _log("Running OpenClaw in the isolated session.");

            int executorExitCode = await _backend.ExecuteAttachedAsync(
                record.ToSandboxIdOrThrow(),
                new MxcExecutionRequest(commandLine),
                null,
                cancellationToken).ConfigureAwait(false);

            return ReadOutcome(resultPath, executorExitCode, requestId);
        }
        finally
        {
            // Only this invocation's files are removed. Concurrent invocations
            // own differently named requests in the same shared workspace.
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private static List<string> BuildNodeArguments(
        SessionExecutionRequest request)
    {
        string entryPoint = Path.Combine(request.ApplicationDirectory, "openclaw.mjs");
        var arguments = new List<string>(request.Arguments.Count + 1) { entryPoint };
        arguments.AddRange(request.Arguments);
        return arguments;
    }

    /// <summary>
    /// Builds the only command line the backend ever sees.
    /// </summary>
    /// <remarks>
    /// Both values are paths this package controls, but they are still verified
    /// rather than trusted. The pinned runtime flattens this string through
    /// <c>cmd.exe</c>, which expands <c>%VAR%</c> and lets an embedded quote
    /// truncate the rest of the line, so a path containing either character is
    /// refused instead of being silently corrupted.
    /// </remarks>
    internal static string BuildGuestCommandLine(
        string helperPath,
        string requestPath,
        string option = "--request")
    {
        Verify(helperPath, nameof(helperPath));
        Verify(requestPath, nameof(requestPath));
        return $"\"{helperPath}\" {option} \"{requestPath}\"";

        static void Verify(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new SessionException($"The {name} is empty.");
            }

            if (value.Contains('"', StringComparison.Ordinal) ||
                value.Contains('%', StringComparison.Ordinal))
            {
                throw new SessionException(
                    $"The {name} contains a quote or percent sign, which the " +
                    "isolated-session command line cannot carry safely: " +
                    value);
            }
        }
    }

    /// <summary>
    /// Establishes what actually happened from the helper's control result.
    /// </summary>
    /// <remarks>
    /// The executor's exit code alone is ambiguous. The helper's failure code
    /// is a value OpenClaw itself can return, and an attached execution
    /// captures nothing, so a dispatch failure is indistinguishable from an
    /// application exit without this file.
    /// </remarks>
    private static int ReadOutcome(string resultPath, int executorExitCode, string requestId)
    {
        string resultText;
        try
        {
            resultText = File.ReadAllText(resultPath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SessionException(
                "The isolated session did not report a launch result " +
                $"(executor exit code {executorExitCode}). OpenClaw may not " +
                "have started.");
        }

        SessionLaunchResult result;
        try
        {
            result = SessionLaunchProtocol.ReadResult(resultText);
        }
        catch (SessionLaunchException exception)
        {
            throw new SessionException(
                $"The isolated session reported an unreadable launch result: " +
                $"{exception.Message}",
                exception);
        }

        if (!result.Launched)
        {
            throw new SessionException(
                "OpenClaw could not be started inside the isolated session: " +
                (result.Error ?? "no reason was reported."));
        }

        if (!string.Equals(result.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new SessionException(
                "The isolated session reported a launch result for a " +
                "different request.");
        }

        return result.ExitCode ?? executorExitCode;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            DirectoryNotFoundException)
        {
        }
    }
}
