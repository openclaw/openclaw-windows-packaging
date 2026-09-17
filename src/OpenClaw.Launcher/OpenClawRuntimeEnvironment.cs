namespace OpenClaw.Launcher;

/// <summary>
/// The environment every OpenClaw process this package starts must receive.
/// </summary>
/// <remarks>
/// These three variables are what tell OpenClaw that this package owns
/// supervision, repair, and updates. They are built in one place so a
/// foreground launch, a session launch, a supervisor, and a logon launch cannot
/// drift apart and leave one path where OpenClaw supervises or updates itself.
/// </remarks>
internal static class OpenClawRuntimeEnvironment
{
    public const string SupervisorModeVariable = "OPENCLAW_SUPERVISOR_MODE";
    public const string ServiceRepairPolicyVariable = "OPENCLAW_SERVICE_REPAIR_POLICY";
    public const string NoAutoUpdateVariable = "OPENCLAW_NO_AUTO_UPDATE";
    public const string GatewayIsolationVariable = "CLAWCTL_GATEWAY_ISOLATION";

    public const string ExternalValue = "external";
    public const string NoAutoUpdateValue = "1";

    /// <summary>
    /// Every OpenClaw process this package starts runs inside the isolated
    /// session, so the reported isolation state is constant.
    /// </summary>
    public const string GatewayIsolationValue = "enabled";

    private const string ForceColorVariable = "FORCE_COLOR";
    private const string WindowsTerminalSessionVariable = "WT_SESSION";
    private const string NoColorVariable = "NO_COLOR";
    private const string PerfVariable = "OPENCLAW_PERF";
    private const string LogLevelVariable = "OPENCLAW_LOG_LEVEL";
    private const string GatewayStartupTraceVariable = "OPENCLAW_GATEWAY_STARTUP_TRACE";
    private const string DiagnosticsVariable = "OPENCLAW_DIAGNOSTICS";
    private const string DiagnosticsEventLoopVariable = "OPENCLAW_DIAGNOSTICS_EVENT_LOOP";
    private const string HandshakeTimeoutVariable = "OPENCLAW_HANDSHAKE_TIMEOUT_MS";

    /// <summary>
    /// The variables to apply, as an ordinary dictionary.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SupervisorModeVariable] = ExternalValue,
            [ServiceRepairPolicyVariable] = ExternalValue,
            [NoAutoUpdateVariable] = NoAutoUpdateValue,
            [GatewayIsolationVariable] = GatewayIsolationValue,
        };

    public static IReadOnlyDictionary<string, string> Build(
        bool isInteractive,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        Dictionary<string, string> result = new(
            Build(),
            StringComparer.OrdinalIgnoreCase);
        string? forceColor = readEnvironmentVariable(ForceColorVariable);
        string? wtSession = readEnvironmentVariable(WindowsTerminalSessionVariable);
        string? noColor = readEnvironmentVariable(NoColorVariable);

        AddIfPresent(result, ForceColorVariable, forceColor);
        AddIfPresent(result, WindowsTerminalSessionVariable, wtSession);
        AddIfPresent(result, NoColorVariable, noColor);

        if (isInteractive)
        {
            if (forceColor is null && noColor is null)
            {
                result[ForceColorVariable] = "3";
            }

            if (wtSession is null)
            {
                result[WindowsTerminalSessionVariable] = "1";
            }
        }

        string? perf = readEnvironmentVariable(PerfVariable);
        string? logLevel = readEnvironmentVariable(LogLevelVariable);
        string? gatewayStartupTrace = readEnvironmentVariable(GatewayStartupTraceVariable);
        string? diagnostics = readEnvironmentVariable(DiagnosticsVariable);
        string? diagnosticsEventLoop = readEnvironmentVariable(DiagnosticsEventLoopVariable);
        string? handshakeTimeout = readEnvironmentVariable(HandshakeTimeoutVariable);
        AddIfPresent(result, PerfVariable, perf);
        AddIfPresent(result, LogLevelVariable, logLevel);
        AddIfPresent(result, GatewayStartupTraceVariable, gatewayStartupTrace);
        AddIfPresent(result, DiagnosticsVariable, diagnostics);
        AddIfPresent(result, DiagnosticsEventLoopVariable, diagnosticsEventLoop);
        AddIfPresent(result, HandshakeTimeoutVariable, handshakeTimeout);

        if (perf == "1")
        {
            AddDefault(result, LogLevelVariable, logLevel, "debug");
            AddDefault(result, GatewayStartupTraceVariable, gatewayStartupTrace, "1");
            AddDefault(result, DiagnosticsVariable, diagnostics, "1");
            AddDefault(result, DiagnosticsEventLoopVariable, diagnosticsEventLoop, "1");
            AddDefault(result, HandshakeTimeoutVariable, handshakeTimeout, "60000");
        }

        return result;
    }

    /// <summary>
    /// Applies the variables to a process environment.
    /// </summary>
    public static void ApplyTo(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach ((string name, string value) in Build())
        {
            environment[name] = value;
        }
    }

    public static void ApplyTo(
        IDictionary<string, string?> environment,
        bool isInteractive,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach ((string name, string value) in Build(isInteractive, readEnvironmentVariable))
        {
            environment[name] = value;
        }
    }

    private static void AddIfPresent(
        Dictionary<string, string> environment,
        string name,
        string? value)
    {
        if (value is not null)
        {
            environment[name] = value;
        }
    }

    private static void AddDefault(
        Dictionary<string, string> environment,
        string name,
        string? currentValue,
        string value)
    {
        if (currentValue is null)
        {
            environment[name] = value;
        }
    }
}
