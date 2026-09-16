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
    string WorkingDirectory)
{
    /// <summary>
    /// Environment determined by the host entrypoint for this invocation.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AdditionalEnvironment { get; init; }
}

/// <summary>
/// An arbitrary foreground command to run inside the session.
/// </summary>
/// <remarks>
/// This is the general form of <see cref="SessionExecutionRequest"/>: the
/// diagnostic commands need to run a shell or the guest helper's own
/// collection mode, neither of which goes through Node. The argument vector
/// still travels as JSON, so nothing here reaches the backend command line.
/// </remarks>
internal sealed record SessionCommandRequest(
    string HelperPath,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    /// <summary>
    /// Values merged over the shared runtime environment for this command only.
    /// </summary>
    /// <remarks>
    /// Kept per request rather than added to the shared environment because
    /// these describe how one command was invoked, and OpenClaw itself must not
    /// see a variable that only the shell's shim needs.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? AdditionalEnvironment { get; init; }
}

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
    private readonly Func<IReadOnlyDictionary<string, string>> _buildEnvironment;
    private readonly Func<string> _createRequestId;
    private readonly Func<SessionRecord, bool> _isCurrentRecord;

    public SessionExecutor(IMxcSessionClient backend, Action<string> log,
        Func<IReadOnlyDictionary<string, string>>? buildEnvironment = null,
        Func<string>? createRequestId = null,
        Func<SessionRecord, bool>? isCurrentRecord = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(log);
        _backend = backend;
        _log = log;
        _buildEnvironment = buildEnvironment ?? (() => OpenClawRuntimeEnvironment.Build());
        _createRequestId = createRequestId ?? (() => Guid.NewGuid().ToString("N"));
        _isCurrentRecord = isCurrentRecord ?? (_ => true);
    }

    public async Task<int> ExecuteAsync(
        SessionRecord record,
        SessionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(request);

        return await ExecuteCommandAsync(
            record,
            new SessionCommandRequest(
                request.HelperPath,
                request.NodePath,
                BuildNodeArguments(request),
                request.WorkingDirectory)
            {
                AdditionalEnvironment = request.AdditionalEnvironment
            },
            "Running OpenClaw in the isolated session.",
            "OpenClaw",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one arbitrary foreground command inside the session and returns its
    /// exit code.
    /// </summary>
    /// <remarks>
    /// The caller supplies the message reported before dispatch and the subject
    /// name used in failures, so a shell that never started does not report
    /// itself as OpenClaw.
    /// </remarks>
    public async Task<int> ExecuteCommandAsync(
        SessionRecord record,
        SessionCommandRequest request,
        string startingMessage,
        string subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(request);

        string requestId = _createRequestId();
        using var operation = new SessionWorkspaceOperation(record, _isCurrentRecord);
        string requestPath = operation.FilePath("launch", requestId);
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);

        var launchRequest = new SessionLaunchRequest
        {
            RequestId = requestId,
            Executable = request.Executable,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = MergeEnvironment(
                _buildEnvironment(), request.AdditionalEnvironment),
        };

        try
        {
            await operation.WriteTextNewAsync(
                requestPath,
                SessionLaunchProtocol.SerializeRequest(launchRequest),
                cancellationToken).ConfigureAwait(false);

            string commandLine = BuildGuestCommandLine(request.HelperPath, requestPath);
            _log(startingMessage);

            int executorExitCode = await _backend.ExecuteAttachedAsync(
                record.ToSandboxIdOrThrow(),
                new MxcExecutionRequest(commandLine),
                null,
                cancellationToken).ConfigureAwait(false);

            operation.EnsureCurrent();
            return ReadOutcome(operation, resultPath, executorExitCode, requestId, subject);
        }
        finally
        {
            // Only this invocation's files are removed. Concurrent invocations
            // own differently named requests in the same shared workspace.
            operation.Delete(requestPath);
            operation.Delete(resultPath);
        }
    }

    /// <summary>Installs the package's Node.js runtime in the agent profile.</summary>
    public async Task<SessionRuntimeInstallResult> InstallRuntimeAsync(
        SessionRecord record,
        string helperPath,
        string archivePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        string requestId = _createRequestId();
        using var operation = new SessionWorkspaceOperation(record, _isCurrentRecord);
        string requestPath = operation.FilePath("runtime", requestId);
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);

        try
        {
            await operation.WriteTextNewAsync(
                requestPath,
                SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
                {
                    RequestId = requestId,
                    ArchivePath = archivePath
                }),
                cancellationToken).ConfigureAwait(false);

            _log("Installing the packaged Node.js runtime in the isolated session.");

            MxcExecutionResult execution = await _backend.ExecuteAsync(
                record.ToSandboxIdOrThrow(),
                new MxcExecutionRequest(
                    BuildGuestCommandLine(helperPath, requestPath, "--install-runtime")),
                null,
                cancellationToken).ConfigureAwait(false);

            string resultText;
            try
            {
                resultText = await operation.ReadTextAsync(resultPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new SessionException(
                    "The isolated session did not report a runtime install " +
                    DescribeMissingResult(execution));
            }

            SessionRuntimeInstallResult result =
                SessionRuntimeProtocol.ReadResult(resultText);
            if (result.Error is { Length: > 0 } error)
            {
                throw new SessionException(
                    $"The packaged Node.js runtime could not be installed in the session: {error}");
            }

            if (!string.Equals(result.RequestId, requestId, StringComparison.Ordinal))
            {
                throw new SessionException(
                    "The isolated session reported a runtime install result " +
                    "for a different request.");
            }

            if (string.IsNullOrWhiteSpace(result.ExecutablePath) ||
                string.IsNullOrWhiteSpace(result.Version))
            {
                throw new SessionException(
                    "The isolated session reported a runtime install without a " +
                    "Node.js executable path and version.");
            }

            return result;
        }
        finally
        {
            operation.Delete(requestPath);
            operation.Delete(resultPath);
        }
    }

    internal static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> baseEnvironment,
        IReadOnlyDictionary<string, string>? additional)
    {
        ArgumentNullException.ThrowIfNull(baseEnvironment);

        if (additional is null || additional.Count == 0)
        {
            return baseEnvironment;
        }

        var merged = new Dictionary<string, string>(
            baseEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in additional)
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    /// <summary>Installs the agent command shim under the guest identity.</summary>
    public async Task<SessionToolInstallResult> InstallToolsAsync(
        SessionRecord record,
        string helperPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        string requestId = _createRequestId();
        using var operation = new SessionWorkspaceOperation(record, _isCurrentRecord);
        string requestPath = operation.FilePath("tools", requestId);
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        try
        {
            await operation.WriteTextNewAsync(
                requestPath,
                SessionRuntimeProtocol.SerializeToolInstallRequest(new SessionToolInstallRequest
                {
                    RequestId = requestId,
                    WorkspacePath = record.WorkspacePath
                }),
                cancellationToken).ConfigureAwait(false);

            _log("Installing OpenClaw agent tools in the isolated session.");
            MxcExecutionResult execution = await _backend.ExecuteAsync(
                record.ToSandboxIdOrThrow(),
                new MxcExecutionRequest(
                    BuildGuestCommandLine(helperPath, requestPath, "--install-tools")),
                null,
                cancellationToken).ConfigureAwait(false);
            string resultText;
            try
            {
                resultText = await operation.ReadTextAsync(resultPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new SessionException(
                    "The isolated session did not report a tool install " +
                    DescribeMissingResult(execution));
            }

            SessionToolInstallResult result =
                SessionRuntimeProtocol.ReadToolInstallResult(resultText);
            if (!string.Equals(result.RequestId, requestId, StringComparison.Ordinal))
            {
                throw new SessionException(
                    "The isolated session reported a tool install result for a different request.");
            }
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                throw new SessionException(
                    $"The OpenClaw agent tools could not be installed: {result.Error}");
            }
            if (string.IsNullOrWhiteSpace(result.ShimPath))
            {
                throw new SessionException(
                    "The isolated session did not report the installed command shim.");
            }

            return result;
        }
        finally
        {
            operation.Delete(requestPath);
            operation.Delete(resultPath);
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
    /// <para>
    /// The pinned runtime dispatches this string through <c>cmd.exe /c</c>.
    /// When that string contains more than two quote characters, the command
    /// processor strips the first and the last one and keeps the rest, so a
    /// naturally quoted <c>"exe" --request "path"</c> arrives with its
    /// executable path unquoted and fails at the first space. An outer pair is
    /// therefore added deliberately: it is the pair that gets sacrificed, and
    /// the inner quoting survives intact.
    /// </para>
    /// <para>
    /// Both values are paths this package controls, but they are still verified
    /// rather than trusted, because <c>cmd.exe</c> also expands <c>%VAR%</c>
    /// and lets an embedded quote truncate the rest of the line.
    /// </para>
    /// </remarks>
    internal static string BuildGuestCommandLine(
        string helperPath,
        string requestPath,
        string option = "--request")
    {
        Verify(helperPath, nameof(helperPath));
        Verify(requestPath, nameof(requestPath));
        return $"\"\"{helperPath}\" {option} \"{requestPath}\"\"";

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
    private static int ReadOutcome(
        SessionWorkspaceOperation operation,
        string resultPath,
        int executorExitCode,
        string requestId,
        string subject)
    {
        string resultText;
        try
        {
            resultText = operation.ReadTextAsync(resultPath, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            throw new SessionException(
                "The isolated session did not report a launch result " +
                $"(executor exit code {executorExitCode}). {subject} may not " +
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
                $"{subject} could not be started inside the isolated session: " +
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

    private static string DescribeMissingResult(MxcExecutionResult execution)
    {
        string detail = string.Join(
            " ",
            new[] { execution.StandardOutput, execution.StandardError }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(detail)
            ? $"result (executor exit code {execution.ExitCode})."
            : $"result (executor exit code {execution.ExitCode}): {detail}";
    }
}
