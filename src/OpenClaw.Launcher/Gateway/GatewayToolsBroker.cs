using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// The narrow, local protocol used by the Windows Hub. It is deliberately
/// separate from the registry: the pipe is a security boundary, not a file
/// transport. Requests are authenticated before the registry is touched and
/// responses contain only bounded, display-safe state.
/// </summary>
internal sealed class GatewayToolsBrokerServer : IAsyncDisposable
{
    internal const string PipeName = "OpenClaw.GatewayToolsBroker";
    private const int MaximumRequestBytes = 8 * 1024;
    private readonly GatewayToolsBrokerService _service;
    private readonly IGatewayToolsBrokerAuthorizer _authorizer;

    public GatewayToolsBrokerServer(
        GatewayToolsBrokerService service,
        IGatewayToolsBrokerAuthorizer authorizer)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
    }

    /// <summary>
    /// Serves callers until the broker has been idle for the supplied time.
    /// The Hub starts this package entry point on demand, avoiding a permanent
    /// privileged desktop process.
    /// </summary>
    public async Task RunUntilIdleAsync(TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(idleTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await ServeClientAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task ServeClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        BrokerResponse response;
        try
        {
            if (!_authorizer.IsAuthorized(stream))
            {
                response = BrokerResponse.Failure("This OpenClaw Hub is not authorized to manage Gateway tools.");
            }
            else
            {
                string? line = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
                BrokerRequest? request = string.IsNullOrWhiteSpace(line)
                    ? null
                    : JsonSerializer.Deserialize<BrokerRequest>(line, GatewayToolsBrokerJson.Options);
                response = request is null
                    ? BrokerResponse.Failure("The Gateway Tools request was invalid.")
                    : await _service.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            response = BrokerResponse.Failure("The Gateway Tools request was invalid.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            response = BrokerResponse.Failure(GatewayToolsBrokerService.ToSafeError(exception));
        }

        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, GatewayToolsBrokerJson.Options) + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadRequestLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256);
        var buffer = new byte[1];
        while (bytes.Count < MaximumRequestBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString([.. bytes]);
            }

            bytes.Add(buffer[0]);
        }

        return bytes.Count >= MaximumRequestBytes ? null : Encoding.UTF8.GetString([.. bytes]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal interface IGatewayToolsBrokerAuthorizer
{
    bool IsAuthorized(Stream stream);
}

/// <summary>
/// Uses the named-pipe server's impersonation token rather than a caller-owned
/// JSON claim. CurrentUserOnly blocks cross-user callers at pipe creation;
/// this second check binds the pipe to the signed-in owner and is kept behind
/// an interface for deterministic tests.
/// </summary>
internal sealed class GatewayToolsBrokerAuthorizer : IGatewayToolsBrokerAuthorizer
{
    private readonly SecurityIdentifier _owner;

    private GatewayToolsBrokerAuthorizer(SecurityIdentifier owner) => _owner = owner;

    public static GatewayToolsBrokerAuthorizer CreateDefault() => new(
        WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException(
            "The signed-in user's security identifier is unavailable."));

    public bool IsAuthorized(Stream stream)
    {
        if (stream is not NamedPipeServerStream pipe || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            bool authorized = false;
            pipe.RunAsClient(() =>
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                authorized = identity.User?.Equals(_owner) == true;
            });
            return authorized;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed class GatewayToolsBrokerService
{
    private static readonly Regex RegistrationIdPattern = new(
        "^toolreg_[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));
    private readonly GatewayToolRegistry _registry;
    private readonly string _gatewayToolsDirectory;
    private readonly Func<string?> _getWorkspacePath;

    public GatewayToolsBrokerService(
        GatewayToolRegistry registry,
        string gatewayToolsDirectory,
        Func<string?> getWorkspacePath)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _gatewayToolsDirectory = Path.GetFullPath(gatewayToolsDirectory ?? throw new ArgumentNullException(nameof(gatewayToolsDirectory)));
        _getWorkspacePath = getWorkspacePath ?? throw new ArgumentNullException(nameof(getWorkspacePath));
    }

    public Task<BrokerResponse> DispatchAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(Dispatch(request));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(BrokerResponse.Failure(ToSafeError(exception)));
        }
    }

    private BrokerResponse Dispatch(BrokerRequest request)
    {
        if (string.Equals(request.Operation, "listTools", StringComparison.Ordinal))
        {
            return BrokerResponse.Success(_registry.List().Select(ToSummary).ToArray());
        }

        return request.Operation switch
        {
            "registerTool" => Register(request.Payload),
            "scanGatewayTools" => Scan(),
            "verifyTool" => Verify(request.Payload),
            "setToolEnabled" => SetEnabled(request.Payload),
            "unregisterTool" => Unregister(request.Payload),
            "createRuntimeProfile" => CreateRuntimeProfile(request.Payload),
            "startInteractiveSetup" => BrokerResponse.Failure(
                "Interactive setup is not available for this tool yet."),
            _ => BrokerResponse.Failure("The Gateway Tools operation is not supported.")
        };
    }

    private BrokerResponse Register(JsonElement payload)
    {
        RegisterToolPayload? request = payload.Deserialize<RegisterToolPayload>(GatewayToolsBrokerJson.Options);
        if (request is null || string.IsNullOrWhiteSpace(request.Command))
        {
            return BrokerResponse.Failure("Select a valid executable and command alias.");
        }

        GatewayToolSource source = request.Source?.Equals("gatewayToolsFolder", StringComparison.OrdinalIgnoreCase) == true
            ? GatewayToolSource.GatewayTools
            : request.Source?.Equals("desktop", StringComparison.OrdinalIgnoreCase) == true
                ? GatewayToolSource.Desktop
                : throw new ArgumentException("The Gateway Tools source is invalid.");
        if (source != GatewayToolSource.Desktop || string.IsNullOrWhiteSpace(request.SelectedExecutablePath))
        {
            return BrokerResponse.Failure("Select an executable to register.");
        }

        _registry.Register(request.Command, request.SelectedExecutablePath, source);
        return BrokerResponse.OperationSucceeded("The tool was registered. Verify it before enabling it for Gateway use.");
    }

    private BrokerResponse Scan()
    {
        Directory.CreateDirectory(_gatewayToolsDirectory);
        int registered = 0;
        foreach (string executable in Directory.EnumerateFiles(_gatewayToolsDirectory, "*.exe", SearchOption.TopDirectoryOnly))
        {
            string command = Path.GetFileNameWithoutExtension(executable);
            try
            {
                _registry.Register(command, executable, GatewayToolSource.GatewayTools);
                registered++;
            }
            catch (ArgumentException)
            {
                // A filename that is not a safe command is not actionable by
                // the Hub. Do not reflect its path or name back to the caller.
            }
        }

        return BrokerResponse.OperationSucceeded(registered == 0
            ? "No supported executables were found in Gateway Tools."
            : "Gateway Tools were registered. Verify each tool before enabling it.");
    }

    private BrokerResponse Verify(JsonElement payload)
    {
        GatewayToolStatus tool = FindTool(ReadRegistrationId(payload));
        return tool.Availability == GatewayToolAvailability.Ready
            ? BrokerResponse.OperationSucceeded("The executable is available for the next Gateway launch.")
            : BrokerResponse.Failure("The selected executable is no longer available.");
    }

    private BrokerResponse SetEnabled(JsonElement payload)
    {
        string registrationId = ReadRegistrationId(payload);
        bool enabled = payload.TryGetProperty("enabled", out JsonElement value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
        GatewayToolStatus tool = FindTool(registrationId);
        if (enabled && tool.Availability != GatewayToolAvailability.Ready)
        {
            return BrokerResponse.Failure("The selected executable is no longer available.");
        }

        _registry.SetEnabled(tool.Command, enabled);
        return BrokerResponse.OperationSucceeded(enabled
            ? "The tool will be available after the next Gateway launch."
            : "The tool was disabled for future Gateway launches.");
    }

    private BrokerResponse Unregister(JsonElement payload)
    {
        GatewayToolStatus tool = FindTool(ReadRegistrationId(payload));
        _registry.Unregister(tool.Command);
        return BrokerResponse.OperationSucceeded("The tool was unregistered.");
    }

    private BrokerResponse CreateRuntimeProfile(JsonElement payload)
    {
        FindTool(ReadRegistrationId(payload));
        string? workspace = _getWorkspacePath();
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return BrokerResponse.Failure("Set up the isolated Gateway session before creating a runtime profile.");
        }

        _registry.PrepareRuntime(workspace);
        return BrokerResponse.OperationSucceeded("A Gateway runtime profile is ready for this tool.");
    }

    private GatewayToolStatus FindTool(string registrationId) => _registry.List().FirstOrDefault(tool =>
        string.Equals(tool.RegistrationId, registrationId, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("The selected Gateway tool is no longer registered.");

    private static string ReadRegistrationId(JsonElement payload)
    {
        if (!payload.TryGetProperty("registrationId", out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } registrationId ||
            !RegistrationIdPattern.IsMatch(registrationId))
        {
            throw new ArgumentException("The selected Gateway tool is invalid.");
        }

        return registrationId;
    }

    private static GatewayToolSummary ToSummary(GatewayToolStatus tool) => new(
        tool.RegistrationId,
        tool.Command,
        tool.Availability == GatewayToolAvailability.Ready ? "ready" : "unavailable",
        "notCreated",
        "notVerified",
        tool.Enabled,
        tool.Availability == GatewayToolAvailability.Ready ? null : "The selected executable is no longer available.");

    internal static string ToSafeError(Exception exception) => exception switch
    {
        ArgumentException => "The Gateway Tools request was invalid.",
        UnauthorizedAccessException => "Gateway Tools could not access its package-managed state.",
        IOException => "Gateway Tools could not update its package-managed state.",
        _ => "The Gateway Tools broker could not complete the request."
    };
}

internal sealed record GatewayToolSummary(
    string RegistrationId,
    string Command,
    string ExecutableStatus,
    string RuntimeProfileStatus,
    string AuthorizationStatus,
    bool Enabled,
    string? Detail = null);

internal sealed record BrokerRequest(string Operation, JsonElement Payload);
internal sealed record BrokerResponse(object? Value, string? Error)
{
    public static BrokerResponse Success(object value) => new(value, null);
    public static BrokerResponse Failure(string error) => new(null, error);
    public static BrokerResponse OperationSucceeded(string message) =>
        new(new GatewayToolOperationResult(true, message), null);
}

internal sealed record GatewayToolOperationResult(bool Succeeded, string? UserMessage = null);

internal sealed record RegisterToolPayload(
    string? Command,
    string? Source,
    string? SelectedExecutablePath);

internal static class GatewayToolsBrokerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
