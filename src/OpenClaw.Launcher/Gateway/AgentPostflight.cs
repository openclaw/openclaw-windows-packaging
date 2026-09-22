using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Builds and runs the work that follows an <c>openclaw</c> child process.
/// </summary>
/// <remarks>
/// This lives beside the gateway rather than in <c>Program</c> so the agent
/// entry point stays a thin adapter, and so the console-capability resolution
/// the postflight needs is composed once instead of inline in a launch path.
/// </remarks>
internal sealed class AgentPostflight
{
    private readonly AgentGatewayGuidance _guidance;

    internal AgentPostflight(AgentGatewayGuidance guidance) => _guidance = guidance;

    /// <summary>
    /// Composes the postflight for a launch that has already resolved its
    /// session, console capabilities, and environment reader.
    /// </summary>
    public static AgentPostflight Create(
        HostOptions options,
        SessionRuntime runtime,
        SessionRecord record,
        bool interactive,
        Func<string, string?> readEnvironmentVariable,
        Action<string> log,
        TimeProvider? clock,
        Func<string>? getLogonSessionId,
        Func<bool>? errorIsProcessConsoleWriter,
        Func<bool>? errorIsInteractive,
        Func<bool>? supportsUnicode)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(log);

        GatewayController gateway = GatewayRuntime
            .Create(options, runtime.Paths, runtime, log, clock)
            .Controller;
        return new AgentPostflight(new AgentGatewayGuidance(
            runtime.LifecycleLock,
            cancellationToken => runtime.Executor.CheckConfigReadinessAsync(
                record,
                runtime.RequireStagedHelper(record),
                cancellationToken),
            cancellationToken => gateway.GetStatusAsync(
                runtime.HelperPath,
                cancellationToken),
            new GatewayGuidanceStateStore(runtime.Paths.GatewayGuidanceStatePath),
            getLogonSessionId ?? WindowsLogonSession.GetCurrentId,
            log,
            clock,
            target =>
            {
                using GatewayOutput output = PrepareGatewayOutput(
                    target,
                    interactive,
                    readEnvironmentVariable,
                    log,
                    errorIsProcessConsoleWriter,
                    errorIsInteractive,
                    supportsUnicode);
                ClawCtlConsole.WriteGatewayHint(
                    target,
                    output.UseColor,
                    output.UseUnicode);
            },
            async (target, start) =>
            {
                using GatewayOutput output = PrepareGatewayOutput(
                    target,
                    interactive,
                    readEnvironmentVariable,
                    log,
                    errorIsProcessConsoleWriter,
                    errorIsInteractive,
                    supportsUnicode);
                return await ClawCtlConsole.NarrateGatewayStartAsync(
                    target,
                    output.UseColor,
                    interactive,
                    output.UseLiveRendering,
                    start).ConfigureAwait(false);
            },
            (progress, onRunningUnderLock) => gateway.StartWithLockAlreadyHeldAsync(
                runtime.HelperPath,
                CancellationToken.None,
                progress,
                onRunningUnderLock),
            (target, detail) =>
            {
                using GatewayOutput output = PrepareGatewayOutput(
                    target,
                    interactive,
                    readEnvironmentVariable,
                    log,
                    errorIsProcessConsoleWriter,
                    errorIsInteractive,
                    supportsUnicode);
                ClawCtlConsole.WriteGatewayStartWarning(
                    target,
                    detail,
                    output.UseColor,
                    output.UseUnicode);
            },
            readEnvironmentVariable));
    }

    private static GatewayOutput PrepareGatewayOutput(
        TextWriter target,
        bool interactive,
        Func<string, string?> readEnvironmentVariable,
        Action<string> log,
        Func<bool>? errorIsProcessConsoleWriter,
        Func<bool>? errorIsInteractive,
        Func<bool>? supportsUnicode)
    {
        bool selectedStreamIsInteractive =
            errorIsInteractive?.Invoke() ??
            WindowsHostConsole.Instance.IsInteractiveOutput(target);
        bool processConsoleWriter =
            errorIsProcessConsoleWriter?.Invoke() ??
            ReferenceEquals(target, Console.Error);
        IDisposable? restore = null;
        bool useColor = ClawCtlColorPolicy.PrepareForegroundOutput(
            noColor: false,
            json: false,
            processConsoleWriter,
            interactive,
            selectedStreamIsInteractive,
            readEnvironmentVariable,
            () => WindowsHostConsole.Instance.TryEnableVirtualTerminalProcessing(
                target,
                log,
                out restore));
        bool useUnicode = supportsUnicode?.Invoke() ??
            (interactive && Console.OutputEncoding.CodePage == 65001);
        bool useLiveRendering = interactive && processConsoleWriter;
        log(
            $"Gateway output capabilities: interactive={interactive}, " +
            $"processStderr={processConsoleWriter}, " +
            $"stderrConsole={selectedStreamIsInteractive}, " +
            $"live={useLiveRendering}, color={useColor}, unicode={useUnicode}.");
        return new GatewayOutput(useColor, useUnicode, useLiveRendering, restore);
    }

    private sealed class GatewayOutput(
        bool useColor,
        bool useUnicode,
        bool useLiveRendering,
        IDisposable? restore) : IDisposable
    {
        public bool UseColor { get; } = useColor;
        public bool UseUnicode { get; } = useUnicode;
        public bool UseLiveRendering { get; } = useLiveRendering;

        public void Dispose() => restore?.Dispose();
    }

    /// <summary>
    /// Runs the postflight. It reports the OpenClaw exit code unchanged: this
    /// path exists to help the user, never to fail their command.
    /// </summary>
    public async Task<int> RunAsync(
        int openClawExitCode,
        bool interactive,
        TextWriter error)
    {
        await _guidance.EvaluateAsync(openClawExitCode, interactive, error)
            .ConfigureAwait(false);
        return openClawExitCode;
    }
}
