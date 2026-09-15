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

    public SessionExecutor(IMxcSessionClient backend, Action<string> log,
        Func<IReadOnlyDictionary<string, string>>? buildEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(log);
        _backend = backend;
        _log = log;
        _buildEnvironment = buildEnvironment ?? OpenClawRuntimeEnvironment.Build;
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
                request.WorkingDirectory),
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
            Executable = request.Executable,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = MergeEnvironment(
                _buildEnvironment(), request.AdditionalEnvironment),
        };

        try
        {
            await File.WriteAllTextAsync(
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

            return ReadOutcome(resultPath, executorExitCode, requestId, subject);
        }
        finally
        {
            // Only this invocation's files are removed. Concurrent invocations
            // own differently named requests in the same shared workspace.
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    /// <summary>Asks the guest to stage diagnostics into the shared workspace.</summary>
    public async Task<SessionCollectResult> CollectAsync(
        SessionRecord record,
        string helperPath,
        string destinationDirectory,
        IReadOnlyList<SessionCollectSource> sources,
        IReadOnlyList<string> deniedNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.WorkspacePath))
        {
            throw new SessionException(
                "The recorded session has no shared workspace, so a collect " +
                "request cannot be delivered to it.");
        }

        string requestId = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(
            record.WorkspacePath,
            $"collect-{requestId}.json");
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);

        try
        {
            await File.WriteAllTextAsync(
                requestPath,
                SessionCollectProtocol.SerializeRequest(new SessionCollectRequest
                {
                    RequestId = requestId,
                    DestinationDirectory = destinationDirectory,
                    Sources = sources,
                    DeniedNames = deniedNames
                }),
                cancellationToken).ConfigureAwait(false);

            _log("Collecting agent-side diagnostics from the isolated session.");

            int executorExitCode = await _backend.ExecuteAttachedAsync(
                record.ToSandboxIdOrThrow(),
                new MxcExecutionRequest(
                    BuildGuestCommandLine(helperPath, requestPath, "--collect")),
                null,
                cancellationToken).ConfigureAwait(false);

            string resultText;
            try
            {
                resultText = await File.ReadAllTextAsync(resultPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new SessionException(
                    "The isolated session did not report a collection result " +
                    $"(executor exit code {executorExitCode}).");
            }

            SessionCollectResult result = SessionCollectProtocol.ReadResult(resultText);
            if (result.Error is { Length: > 0 } error)
            {
                throw new SessionException(
                    $"The isolated session could not collect diagnostics: {error}");
            }

            if (!string.Equals(result.RequestId, requestId, StringComparison.Ordinal))
            {
                throw new SessionException(
                    "The isolated session reported a collection result for a " +
                    "different request.");
            }

            return result;
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    /// <summary>
    /// Installs the packaged Node.js runtime into the agent's profile.
    /// </summary>
    /// <remarks>
    /// Setup invokes this explicitly rather than leaving the first gateway
    /// launch to discover a missing agent runtime.
    /// </remarks>
    public async Task<SessionRuntimeInstallResult> InstallRuntimeAsync(
        SessionRecord record,
        string helperPath,
        string archivePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (string.IsNullOrWhiteSpace(record.WorkspacePath))
        {
            throw new SessionException(
                "The recorded session has no shared workspace, so a runtime " +
                "install request cannot be delivered to it.");
        }

        string requestId = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(record.WorkspacePath, $"runtime-{requestId}.json");
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);

        try
        {
            await File.WriteAllTextAsync(
                requestPath,
                SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
                {
                    RequestId = requestId,
                    ArchivePath = archivePath
                }),
                cancellationToken).ConfigureAwait(false);

            _log("Installing the packaged Node.js runtime in the isolated session.");

            int executorExitCode = await _backend.ExecuteAttachedAsync(
                record.ToSandboxIdOrThrow(),
                new MxcExecutionRequest(
                    BuildGuestCommandLine(helperPath, requestPath, "--install-runtime")),
                null,
                cancellationToken).ConfigureAwait(false);

            string resultText;
            try
            {
                resultText = await File.ReadAllTextAsync(resultPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new SessionException(
                    "The isolated session did not report a runtime install " +
                    $"result (executor exit code {executorExitCode}).");
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
            TryDelete(requestPath);
            TryDelete(resultPath);
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
        string resultPath, int executorExitCode, string requestId, string subject)
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
