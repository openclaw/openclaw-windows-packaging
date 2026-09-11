using System.Reflection;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        HostEntrypoint entrypoint = HostEntrypointResolver.Resolve();
        string commandName = entrypoint == HostEntrypoint.Control
            ? HostEntrypointResolver.ControlCommandName
            : HostEntrypointResolver.AgentCommandName;
        HostDiagnosticLog? diagnostics = null;
        bool diagnosticWarningWritten = false;
        bool consoleWarningWritten = false;

        void WriteConsoleError(string message)
        {
            try
            {
                Console.Error.WriteLine(message);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException)
            {
                if (!consoleWarningWritten)
                {
                    consoleWarningWritten = true;
                    WriteDiagnostic(
                        $"Console error output failed: {exception.GetType().Name}.");
                }
            }
        }

        try
        {
            diagnostics = HostDiagnosticLog.Create();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            diagnosticWarningWritten = true;
            WriteConsoleError(
                $"{commandName}: Unable to create diagnostics: {exception.Message}");
        }

        void WriteDiagnostic(string message)
        {
            if (diagnostics is null)
            {
                return;
            }

            try
            {
                diagnostics.Write(message);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                ObjectDisposedException)
            {
                if (!diagnosticWarningWritten)
                {
                    diagnosticWarningWritten = true;
                    WriteConsoleError(
                        $"{commandName}: Unable to write diagnostics: {exception.Message}");
                }
            }
        }

        static string GetDiagnosticFailure(Exception exception) =>
            exception switch
            {
                InvalidDataException or
                TimeoutException or
                PlatformNotSupportedException or
                FileNotFoundException =>
                    $"{exception.GetType().Name}: {exception.Message}",
                _ => exception.GetType().Name
            };

        try
        {
            WriteDiagnostic($"Host started through the {commandName} entrypoint.");
            HostOptions options = HostOptions.Parse(args);
            return entrypoint == HostEntrypoint.Control
                ? await RunControlAsync(
                    options,
                    args,
                    WriteDiagnostic,
                    WriteConsoleError,
                    Console.Out)
                : await RunAgentAsync(options, WriteDiagnostic);
        }
        catch (Exception exception)
        {
            WriteDiagnostic($"Unhandled failure: {GetDiagnosticFailure(exception)}");
            WriteConsoleError($"{commandName}: {exception.Message}");
            if (diagnostics is not null)
            {
                WriteConsoleError(
                    $"{commandName}: See diagnostics: {diagnostics.Path}");
            }
            return 1;
        }
        finally
        {
            WriteDiagnostic("Host exiting.");
            diagnostics?.Dispose();
        }
    }

    internal static async Task<int> RunAgentAsync(
        HostOptions options,
        Action<string> log,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null) =>
        await RunAgentAsync(
            options,
            log,
            resolveNode ?? NodeRuntimeResolver.ResolveAsync,
            GatewayLauncher.RunAsync,
            DecideSessionRoutingAsync,
            ExecuteInSessionAsync);

    // launchOpenClaw is a test seam: tests substitute a fake in place of
    // GatewayLauncher.RunAsync so they can assert launch behavior without
    // starting a real Node child process or Windows job object.
    // decideRouting and runInSession are seams for the same reason: neither a
    // real MXC backend nor a packaged identity exists during tests.
    internal static async Task<int> RunAgentAsync(
        HostOptions options,
        Action<string> log,
        Func<CancellationToken, Task<NodeRuntime>> resolveNode,
        LaunchOpenClawAsync launchOpenClaw,
        Func<CancellationToken, Task<SessionRoutingDecision>> decideRouting,
        RunInSessionAsync runInSession)
    {
        NodeRuntime nodeRuntime = await resolveNode(CancellationToken.None);
        log(
            $"Using Node.js {nodeRuntime.Version} from " +
            $"{nodeRuntime.ExecutablePath}.");
        string applicationDirectory = GetPackagedApplicationDirectory(options);

        SessionRoutingDecision decision =
            await decideRouting(CancellationToken.None).ConfigureAwait(false);
        if (decision.Routing == SessionRouting.Session)
        {
            log($"Using an isolated session: {decision.Reason}");

            // There is no fallback from here. A backend that fails on a
            // supported machine must surface rather than quietly relocating
            // the user's work onto the host, where the profile and isolation
            // both differ.
            return await runInSession(
                nodeRuntime,
                applicationDirectory,
                options.OpenClawArguments,
                log,
                CancellationToken.None).ConfigureAwait(false);
        }

        log($"Running OpenClaw directly on the host: {decision.Reason}");
        log("Using the OpenClaw application directly from the package.");
        return await launchOpenClaw(
            nodeRuntime.ExecutablePath,
            applicationDirectory,
            options.OpenClawArguments,
            CancellationToken.None,
            log);
    }

    private static async Task<SessionRoutingDecision> DecideSessionRoutingAsync(
        CancellationToken cancellationToken)
    {
        SessionMode mode = SessionRoutingPolicy.ReadMode(
            Environment.GetEnvironmentVariable);

        // The readiness probe is skipped when sessions are switched off, so a
        // disabled installation never pays for it or fails because of it.
        MxcReadinessReport readiness = mode == SessionMode.Disabled
            ? MxcReadiness.Unavailable("Isolated sessions are disabled.")
            : await MxcReadiness.ProbeAsync(cancellationToken).ConfigureAwait(false);

        return SessionRoutingPolicy.Decide(
            mode,
            PackageIdentity.TryGetPackageFamilyName(),
            readiness);
    }

    private static async Task<int> ExecuteInSessionAsync(
        NodeRuntime nodeRuntime,
        string applicationDirectory,
        IReadOnlyList<string> openClawArguments,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        SessionRuntime host = SessionRuntime.Create(log);
        SessionRecord record = await host.Coordinator
            .EnsureStartedAsync(cancellationToken)
            .ConfigureAwait(false);

        return await host.Executor.ExecuteAsync(
            record,
            new SessionExecutionRequest(
                host.HelperPath,
                nodeRuntime.ExecutablePath,
                applicationDirectory,
                openClawArguments,
                Environment.CurrentDirectory),
            cancellationToken).ConfigureAwait(false);
    }

    // output is a required parameter (not a Console.Out default) so tests
    // can capture clawctl output without mutating global console state,
    // which would be unsafe across parallel test runs.
    internal static async Task<int> RunControlAsync(
        HostOptions options,
        IReadOnlyList<string> args,
        Action<string> log,
        Action<string> writeError,
        TextWriter output,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null,
        Func<CancellationToken, Task<MxcReadinessReport>>? probeMxcReadiness = null,
        Func<Action<string>, SessionCoordinator>? createSessionCoordinator = null,
        Func<HostOptions, Action<string>, GatewayRuntime>? createGatewayRuntime = null)
    {
        ClawCtlCommandParseResult parsed = ClawCtlCommandParser.Parse(args);
        if (parsed.Error is not null)
        {
            writeError($"clawctl: {parsed.Error}");
            ClawCtlConsole.WriteUsage(Console.Error);
            return 2;
        }

        switch (parsed.Command)
        {
            case ClawCtlCommand.Help:
                ClawCtlConsole.WriteHelp(output);
                return 0;
            case ClawCtlCommand.Version:
                output.WriteLine(
                    Assembly.GetExecutingAssembly().GetName().Version?.ToString() ??
                    "unknown");
                return 0;
            case ClawCtlCommand.Setup:
            {
                NodeRuntime nodeRuntime = await (
                    resolveNode ?? NodeRuntimeResolver.ResolveAsync)(
                        CancellationToken.None);
                ClawCtlConsole.WriteNodeRuntimeSummary(output, nodeRuntime);
                string applicationDirectory =
                    GetPackagedApplicationDirectory(options);
                log("Confirmed the packaged OpenClaw application is present.");
                ClawCtlConsole.WriteReadinessSummary(
                    output,
                    applicationDirectory);
                MxcReadinessReport readiness = await
                    (probeMxcReadiness ?? MxcReadiness.ProbeAsync)(
                        CancellationToken.None).ConfigureAwait(false);
                ClawCtlConsole.WriteMxcReadinessSummary(output, readiness);
                log(
                    "MXC runtime available: " +
                    $"{readiness.RuntimeAvailable}; host support: " +
                    $"{readiness.HostSupport} " +
                    $"(evidence: {readiness.SupportEvidence}).");
                return 0;
            }
            case ClawCtlCommand.SessionStatus:
            {
                SessionCoordinator coordinator =
                    CreateCoordinator(createSessionCoordinator, log);
                ClawCtlConsole.WriteSessionStatus(
                    output,
                    coordinator.GetRecordedStatus());
                return 0;
            }
            case ClawCtlCommand.SessionStop:
            {
                SessionCoordinator coordinator =
                    CreateCoordinator(createSessionCoordinator, log);
                bool stopped = await coordinator
                    .StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                ClawCtlConsole.WriteSessionStopped(output, stopped);
                return 0;
            }
            case ClawCtlCommand.SessionRemove:
            {
                SessionCoordinator coordinator =
                    CreateCoordinator(createSessionCoordinator, log);
                SessionRemovalResult removal = await coordinator
                    .RemoveAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                ClawCtlConsole.WriteSessionRemoved(output, removal);
                return 0;
            }
            case ClawCtlCommand.GatewayStatus:
            {
                GatewayRuntime gateway = CreateGateway(createGatewayRuntime, options, log);

                // Read-only: nothing is started, provisioned, registered, or
                // written by asking what the state is.
                GatewayStatusReport report = await gateway.Controller
                    .GetStatusAsync(gateway.HelperPath, CancellationToken.None)
                    .ConfigureAwait(false);
                GatewayPersistenceStatus persistence = await gateway.Persistence
                    .GetStatusAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                ClawCtlConsole.WriteGatewayStatus(output, report, persistence);
                return 0;
            }
            case ClawCtlCommand.GatewayInstall:
            case ClawCtlCommand.GatewayStart:
            {
                GatewayRuntime gateway = CreateGateway(createGatewayRuntime, options, log);
                GatewayStartResult started = await gateway.Controller
                    .StartAsync(gateway.HelperPath, CancellationToken.None)
                    .ConfigureAwait(false);
                ClawCtlConsole.WriteGatewayStarted(output, started);

                // Startup succeeds only when both the gateway and its sign-in
                // recovery succeed, so a half-configured install is never
                // reported as success.
                return started.Persistence is { } persistence &&
                       persistence.State is GatewayPersistenceState.ActionRequired
                                         or GatewayPersistenceState.Unknown
                    ? 1
                    : 0;
            }
            case ClawCtlCommand.GatewayStop:
            {
                GatewayRuntime gateway = CreateGateway(createGatewayRuntime, options, log);
                GatewayStopResult stopped = await gateway.Controller
                    .StopAsync(gateway.HelperPath, CancellationToken.None)
                    .ConfigureAwait(false);
                ClawCtlConsole.WriteGatewayStopped(output, stopped);
                return 0;
            }
            case ClawCtlCommand.GatewayUninstall:
            {
                GatewayRuntime gateway = CreateGateway(createGatewayRuntime, options, log);

                // Stop first: removing the task while the gateway is still
                // running would leave a process nothing is recorded as owning.
                GatewayStopResult stop = await gateway.Controller
                    .StopAsync(gateway.HelperPath, CancellationToken.None)
                    .ConfigureAwait(false);
                GatewayPersistenceRemovalResult removed = await gateway.Persistence
                    .UninstallAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                ClawCtlConsole.WriteGatewayUninstalled(output, stop, removed);
                return removed.Succeeded ? 0 : 1;
            }
            default:
                throw new InvalidOperationException("Unknown clawctl command.");
        }
    }

    private static GatewayRuntime CreateGateway(
        Func<HostOptions, Action<string>, GatewayRuntime>? factory,
        HostOptions options,
        Action<string> log) =>
        factory is not null
            ? factory(options, log)
            : GatewayRuntime.Create(options, log);

    private static SessionCoordinator CreateCoordinator(
        Func<Action<string>, SessionCoordinator>? factory,
        Action<string> log) =>
        factory is not null
            ? factory(log)
            : SessionRuntime.Create(log).Coordinator;

    internal delegate Task<int> LaunchOpenClawAsync(
        string nodePath,
        string applicationDirectory,
        IReadOnlyList<string> openClawArguments,
        CancellationToken cancellationToken,
        Action<string>? log);

    internal delegate Task<int> RunInSessionAsync(
        NodeRuntime nodeRuntime,
        string applicationDirectory,
        IReadOnlyList<string> openClawArguments,
        Action<string> log,
        CancellationToken cancellationToken);

    private static string GetPackagedApplicationDirectory(HostOptions options)
    {
        // Re-check File.Exists here (HostOptions.Parse already checked it)
        // so both a never-resolved and a since-removed application directory
        // fail through the same FileNotFoundException message.
        string? applicationDirectory = options.PackagedApplicationDirectory;
        string entryPoint = Path.Combine(
            applicationDirectory ?? Path.Combine(AppContext.BaseDirectory, "app"),
            "openclaw.mjs");
        if (applicationDirectory is null || !File.Exists(entryPoint))
        {
            throw new FileNotFoundException(
                "The packaged OpenClaw entry point was not found.",
                entryPoint);
        }

        return applicationDirectory;
    }
}
