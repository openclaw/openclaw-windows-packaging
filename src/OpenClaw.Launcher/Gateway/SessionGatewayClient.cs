using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Gateway;

/// <summary>What to start inside the session.</summary>
/// <remarks>
/// <see cref="Port"/> is null unless the user pinned one, in which case
/// OpenClaw resolves the port from its own configuration.
/// </remarks>
internal sealed record GatewayStartRequest(
    string HelperPath,
    string NodePath,
    string ApplicationDirectory,
    int? Port);

/// <summary>The identity of a gateway that was started.</summary>
internal sealed record GatewayStartOutcome(
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    string StatusPath,
    string LogPath)
{
    public string? HelperPath { get; init; }
}

/// <summary>
/// Starts, inspects, and stops the gateway inside the owned session.
/// </summary>
internal interface ISessionGatewayClient
{
    Task<GatewayStartOutcome> StartAsync(
        SessionRecord session,
        GatewayStartRequest request,
        CancellationToken cancellationToken);

    Task<SessionInspectResult> InspectAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        CancellationToken cancellationToken);

    Task<SessionInspectResult> StopAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Starts and inspects the gateway inside the owned session.
/// </summary>
/// <remarks>
/// Only controlled paths ever reach the backend's command line. Everything the
/// gateway is actually launched with travels as request data, for the same
/// reason ordinary OpenClaw invocations do: the pinned runtime flattens that
/// command line through <c>cmd.exe</c>.
/// </remarks>
internal sealed class SessionGatewayClient : ISessionGatewayClient
{
    private readonly IMxcSessionClient _backend;
    private readonly Action<string> _log;
    private readonly Func<IReadOnlyDictionary<string, string>> _buildEnvironment;

    public SessionGatewayClient(IMxcSessionClient backend, Action<string> log,
        Func<IReadOnlyDictionary<string, string>>? buildEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(log);
        _backend = backend;
        _log = log;
        _buildEnvironment = buildEnvironment ?? OpenClawRuntimeEnvironment.Build;
    }

    public async Task<GatewayStartOutcome> StartAsync(
        SessionRecord session,
        GatewayStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        string workspace = RequireWorkspace(session);
        string helperPath = SessionHelperStager.RequireStaged(
            request.HelperPath,
            workspace);

        // Each launch gets its own unguessable evidence paths, so a process
        // left behind by an earlier launch cannot write to them and cannot make
        // itself look like this generation.
        string generation = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(workspace, $"gateway-{generation}.json");
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string statusPath = Path.Combine(workspace, $"gateway-{generation}.status.json");
        string logPath = Path.Combine(workspace, $"gateway-{generation}.log");

        string entryPoint = Path.Combine(request.ApplicationDirectory, "openclaw.mjs");

        // --port is added only when the user pinned one. Passing a port always
        // would outrank `gateway.port` in OpenClaw's own configuration and
        // silently move the gateway away from where its clients look.
        List<string> arguments = [entryPoint, "gateway", "run"];
        if (request.Port is int port)
        {
            arguments.Add("--port");
            arguments.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var launch = new SessionLaunchRequest
        {
            RequestId = generation,
            Mode = SessionLaunchMode.Detached,
            Executable = request.NodePath,
            Arguments = arguments,
            WorkingDirectory = workspace,
            Environment = _buildEnvironment(),
            PathPrefix = Path.GetDirectoryName(request.NodePath)
                ?? throw new SessionException(
                    "The agent's Node.js runtime has no parent directory."),
            LogPath = logPath,
            StatusPath = statusPath
        };

        try
        {
            await File.WriteAllTextAsync(
                requestPath,
                SessionLaunchProtocol.SerializeRequest(launch),
                cancellationToken).ConfigureAwait(false);

            _log(request.Port is int chosen
                ? $"Starting the gateway in the isolated session on port {chosen}."
                : "Starting the gateway in the isolated session on its configured port.");

            MxcExecutionResult execution = await _backend.ExecuteAsync(
                session.ToSandboxIdOrThrow(),
                new MxcExecutionRequest(
                    SessionExecutor.BuildGuestCommandLine(helperPath, requestPath)),
                null,
                cancellationToken).ConfigureAwait(false);

            SessionLaunchResult result = ReadLaunchResult(resultPath, execution);

            if (result.RequestId != generation || execution.ExitCode != 0 ||
                result.ProcessId is not > 0 ||
                result.ProcessStartTimeUtc is not { } startTime || startTime == default)
            {
                // Without both, the process could not be identified again, and a
                // record carrying only an identifier would eventually name an
                // unrelated process.
                throw new SessionException(
                    "The isolated session started the gateway but did not " +
                    "report an identifiable process, so it was not recorded.");
            }

            return new GatewayStartOutcome(result.ProcessId.Value, startTime, statusPath, logPath)
            {
                HelperPath = helperPath
            };
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    /// <summary>
    /// Ends the recorded gateway inside the session.
    /// </summary>
    /// <remarks>
    /// The guest verifies identity before terminating anything, so a recorded
    /// identifier that now belongs to an unrelated process stops nothing.
    /// </remarks>
    public Task<SessionInspectResult> StopAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        CancellationToken cancellationToken) =>
        SendAsync(session, gateway, helperPath, "--stop", cancellationToken);

    /// <summary>
    /// Re-establishes, inside the session, whether the recorded gateway is
    /// still ours and still serving.
    /// </summary>
    public Task<SessionInspectResult> InspectAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        CancellationToken cancellationToken) =>
        SendAsync(session, gateway, helperPath, "--inspect", cancellationToken);

    private async Task<SessionInspectResult> SendAsync(
        SessionRecord session,
        GatewayRecord gateway,
        string helperPath,
        string option,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(gateway);

        string workspace = RequireWorkspace(session);
        string stagedHelperPath = SessionHelperStager.RequireStaged(
            helperPath,
            workspace);
        string requestId = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(workspace, $"inspect-{requestId}.json");
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);

        try
        {
            await File.WriteAllTextAsync(
                requestPath,
                SessionInspectProtocol.SerializeRequest(new SessionInspectRequest
                {
                    RequestId = requestId,
                    ProcessId = gateway.ProcessId,
                    ProcessStartTimeUtc = gateway.ProcessStartTimeUtc,
                    HelperPath = gateway.HelperPath,
                    StatusPath = gateway.StatusPath,
                    Port = gateway.Port
                }),
                cancellationToken).ConfigureAwait(false);

            MxcExecutionResult execution = await _backend.ExecuteAsync(
                session.ToSandboxIdOrThrow(),
                new MxcExecutionRequest(
                    SessionExecutor.BuildGuestCommandLine(
                        stagedHelperPath,
                        requestPath,
                        option)),
                null,
                cancellationToken).ConfigureAwait(false);

            try
            {
                SessionInspectResult result = SessionInspectProtocol.ReadResult(
                    await File.ReadAllTextAsync(resultPath, cancellationToken)
                        .ConfigureAwait(false));
                if (result.RequestId != requestId || execution.ExitCode != 0)
                {
                    return new SessionInspectResult
                    {
                        RequestId = requestId,
                        Error = $"The guest result did not match this invocation (executor exit {execution.ExitCode})."
                    };
                }
                return result;
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException or
                IOException or UnauthorizedAccessException or SessionLaunchException)
            {
                // An unanswered request is reported as unknown. Treating it as
                // "not running" would invite starting a second gateway beside a
                // healthy one, or claiming a stop that never happened.
                return new SessionInspectResult
                {
                    RequestId = requestId,
                    Error =
                        "The isolated session did not report on the gateway: " +
                        exception.Message
                };
            }
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private static string RequireWorkspace(SessionRecord session) =>
        string.IsNullOrWhiteSpace(session.WorkspacePath)
            ? throw new SessionException(
                "The recorded session has no shared workspace, so the gateway " +
                "cannot be started or inspected.")
            : session.WorkspacePath;

    private static SessionLaunchResult ReadLaunchResult(
        string resultPath,
        MxcExecutionResult execution)
    {
        string text;
        try
        {
            text = File.ReadAllText(resultPath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SessionException(
                "The isolated session did not report a gateway launch result " +
                $"(executor exit code {execution.ExitCode}). " +
                Describe(execution));
        }

        SessionLaunchResult result = SessionLaunchProtocol.ReadResult(text);
        if (!result.Launched || result.Error is not null)
        {
            throw new SessionException(
                "The gateway could not be started in the isolated session: " +
                (result.Error ?? "the session helper reported no detail."));
        }

        return result;
    }

    private static string Describe(MxcExecutionResult execution) =>
        string.IsNullOrWhiteSpace(execution.StandardError)
            ? "The session reported no diagnostics."
            : $"Session diagnostics: {execution.StandardError.Trim()}";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Only this invocation's files are removed; a leftover control file
            // is not a reason to fail a gateway that started.
        }
    }
}
