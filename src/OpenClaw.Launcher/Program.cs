using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher;

internal static class Program
{
    public static async Task<int> Main(string[] args) =>
        await RunAsync(args, HostStartup.CreateProduction()).ConfigureAwait(false);

    // The whole startup path lives here rather than in Main so that tests can
    // drive it with fixture-owned diagnostics and writers. Main is only the
    // production adapter that supplies the real collaborators.
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "This is the process last-chance handler. Every narrower catch in " +
            "this assembly uses an exception filter; this one deliberately does " +
            "not, because narrowing it would replace the diagnostic log entry, " +
            "the user-facing error message, and the deterministic exit code 1 " +
            "with an unhandled-exception crash.")]
    internal static async Task<int> RunAsync(string[] args, HostStartup startup)
    {
        string commandName = startup.Entrypoint == HostEntrypoint.Control
            ? HostEntrypointResolver.ControlCommandName
            : HostEntrypointResolver.AgentCommandName;
        HostDiagnosticLog? diagnostics = null;
        bool diagnosticWarningWritten = false;
        bool consoleWarningWritten = false;

        void WriteConsoleError(string message)
        {
            try
            {
                startup.Error.WriteLine(message);
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
            diagnostics = startup.CreateDiagnostics();
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
            HostOptions options = HostOptions.Parse(args, startup.BaseDirectory);
            return startup.Entrypoint == HostEntrypoint.Control
                ? await RunControlAsync(
                    options,
                    args,
                    WriteDiagnostic,
                    startup.Output,
                    startup.Error,
                    startup.ResolveNode,
                    createGatewayRuntime: startup.CreateGatewayRuntime)
                    .ConfigureAwait(false)
                : await RunAgentAsync(
                    options,
                    WriteDiagnostic,
                    startup.ResolveNode ?? NodeRuntimeResolver.ResolveAsync,
                    startup.LaunchOpenClaw ?? GatewayLauncher.RunAsync,
                    startup.DecideRouting ?? DecideSessionRoutingAsync,
                    startup.RunInSession ?? ExecuteInSessionAsync)
                    .ConfigureAwait(false);
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
            ExecuteInSessionAsync).ConfigureAwait(false);

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
        SessionRoutingDecision decision =
            await decideRouting(CancellationToken.None).ConfigureAwait(false);

        NodeRuntime nodeRuntime = await resolveNode(CancellationToken.None)
            .ConfigureAwait(false);
        log(
            $"Using Node.js {nodeRuntime.Version} from " +
            $"{nodeRuntime.ExecutablePath}.");
        string applicationDirectory = GetPackagedApplicationDirectory(options);

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

        log($"Using the OpenClaw application directly from the package: {decision.Reason}");
        return await launchOpenClaw(
            nodeRuntime.ExecutablePath,
            applicationDirectory,
            options.OpenClawArguments,
            CancellationToken.None,
            log).ConfigureAwait(false);
    }

    private static Task<SessionRoutingDecision> DecideSessionRoutingAsync(
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        SessionRuntime runtime = SessionRuntime.Create(_ => { });
        runtime.RequireSetup();
        return Task.FromResult(
            new SessionRoutingDecision(
                SessionRouting.Session,
                "Explicit setup completed for this installation."));
    }

    private static async Task<int> ExecuteInSessionAsync(
        NodeRuntime nodeRuntime,
        string applicationDirectory,
        IReadOnlyList<string> openClawArguments,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        SessionRuntime host = SessionRuntime.Create(log);
        host.RequireSetup();
        SessionRecord record = await host.Coordinator
            .StartRecordedAsync(cancellationToken)
            .ConfigureAwait(false);
        string helperPath = host.RequireStagedHelper(record);

        return await host.Executor.ExecuteAsync(
            record,
            new SessionExecutionRequest(
                helperPath,
                nodeRuntime.ExecutablePath,
                applicationDirectory,
                openClawArguments,
                Environment.CurrentDirectory),
            cancellationToken).ConfigureAwait(false);
    }

    // output and error are required parameters (not Console defaults) so tests
    // can capture clawctl output without mutating global console state,
    // which would be unsafe across parallel test runs.
    internal static async Task<int> RunControlAsync(
        HostOptions options,
        IReadOnlyList<string> args,
        Action<string> log,
        TextWriter output,
        TextWriter error,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null,
        Func<Action<string>, SessionCoordinator>? createSessionCoordinator = null,
        Func<HostOptions, Action<string>, GatewayRuntime>? createGatewayRuntime = null)
    {
        ClawCtlHandlers handlers = new()
        {
            Setup = token => RunSetupAsync(
                options,
                log,
                output,
                resolveNode,
                createGatewayRuntime,
                token),
            SessionStatus = token => RunSessionStatusAsync(
                createSessionCoordinator,
                log,
                output,
                token),
            SessionStop = token => RunSessionStopAsync(
                createSessionCoordinator,
                log,
                output,
                token),
            SessionRemove = token => RunSessionRemoveAsync(
                createSessionCoordinator,
                log,
                output,
                token),
            GatewayInstall = token => RunGatewayStartAsync(
                createGatewayRuntime,
                options,
                log,
                output,
                token),
            GatewayStart = token => RunGatewayStartAsync(
                createGatewayRuntime,
                options,
                log,
                output,
                token),
            GatewayStatus = token => RunGatewayStatusAsync(
                createGatewayRuntime,
                options,
                log,
                output,
                token),
            GatewayStop = token => RunGatewayStopAsync(
                createGatewayRuntime,
                options,
                log,
                output,
                token),
            GatewayUninstall = token => RunGatewayUninstallAsync(
                createGatewayRuntime,
                options,
                log,
                output,
                token),
            GatewayDiagnose = token => RunGatewayDiagnoseAsync(
                createGatewayRuntime,
                options,
                log,
                output,
                resolveNode,
                token)
        };

        RootCommand command = ClawCtlCommandLine.Create(handlers);

        InvocationConfiguration configuration = new()
        {
            Output = output,
            Error = error,

            // Operational failures stay the host's responsibility. The default
            // handler would print its own message and return its own exit code,
            // losing the diagnostic log entry and the log path Main reports.
            EnableDefaultExceptionHandler = false,

            // Node lifetime is owned by the job object in GatewayLauncher. The
            // library's termination timeout would add a second, conflicting
            // forced-exit policy and process-wide signal handlers.
            ProcessTerminationTimeout = null
        };

        return await command
            .Parse(args, ClawCtlCommandLine.CreateParserConfiguration())
            .InvokeAsync(configuration)
            .ConfigureAwait(false);
    }

    private static async Task<int> RunSessionStatusAsync(
        Func<Action<string>, SessionCoordinator>? factory,
        Action<string> log,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        SessionCoordinator coordinator = CreateCoordinator(factory, log);
        ClawCtlConsole.WriteSessionStatus(output, coordinator.GetRecordedStatus());
        return await Task.FromResult(0).ConfigureAwait(false);
    }

    private static async Task<int> RunSessionStopAsync(
        Func<Action<string>, SessionCoordinator>? factory,
        Action<string> log,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        SessionCoordinator coordinator = CreateCoordinator(factory, log);
        bool stopped = await coordinator
            .StopAsync(cancellationToken)
            .ConfigureAwait(false);
        ClawCtlConsole.WriteSessionStopped(output, stopped);
        return 0;
    }

    private static async Task<int> RunSessionRemoveAsync(
        Func<Action<string>, SessionCoordinator>? factory,
        Action<string> log,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        SessionCoordinator coordinator = CreateCoordinator(factory, log);
        SessionRemovalResult removal = await coordinator
            .RemoveAsync(cancellationToken)
            .ConfigureAwait(false);
        ClawCtlConsole.WriteSessionRemoved(output, removal);
        return 0;
    }

    private static async Task<int> RunGatewayStartAsync(
        Func<HostOptions, Action<string>, GatewayRuntime>? factory,
        HostOptions options,
        Action<string> log,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        GatewayRuntime gateway = CreateGateway(factory, options, log);
        GatewayStartResult started = await gateway.Controller
            .StartAsync(gateway.HelperPath, cancellationToken)
            .ConfigureAwait(false);
        ClawCtlConsole.WriteGatewayStarted(output, started);

        // Startup succeeds only when both the gateway and its sign-in recovery
        // succeed, so a half-configured install is never reported as success.
        return started.Persistence is { } persistence &&
               persistence.State is GatewayPersistenceState.ActionRequired
                                 or GatewayPersistenceState.Unknown
            ? 1
            : 0;
    }

    private static async Task<int> RunGatewayStatusAsync(
        Func<HostOptions, Action<string>, GatewayRuntime>? factory,
        HostOptions options,
        Action<string> log,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        GatewayRuntime gateway = CreateGateway(factory, options, log);

        // Read-only: nothing is started, provisioned, registered, or written by
        // asking what the state is.
        GatewayStatusReport report = await gateway.Controller
            .GetStatusAsync(gateway.HelperPath, cancellationToken)
            .ConfigureAwait(false);
        GatewayPersistenceStatus persistence = await gateway.Persistence
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        ClawCtlConsole.WriteGatewayStatus(output, report, persistence);
        return 0;
    }

    private static async Task<int> RunGatewayStopAsync(
        Func<HostOptions, Action<string>, GatewayRuntime>? factory,
        HostOptions options,
        Action<string> log,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        GatewayRuntime gateway = CreateGateway(factory, options, log);
        GatewayStopResult stopped = await gateway.Controller
            .StopAsync(gateway.HelperPath, cancellationToken)
            .ConfigureAwait(false);
        ClawCtlConsole.WriteGatewayStopped(output, stopped);
        return 0;
    }

    private static async Task<int> RunGatewayUninstallAsync(
        Func<HostOptions, Action<string>, GatewayRuntime>? factory,
        HostOptions options,
        Action<string> log,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        GatewayRuntime gateway = CreateGateway(factory, options, log);

        // Stop first: removing the task while the gateway is still running
        // would leave a process nothing is recorded as owning.
        GatewayStopResult stop = await gateway.Controller
            .StopAsync(gateway.HelperPath, cancellationToken)
            .ConfigureAwait(false);
        GatewayPersistenceRemovalResult removed = await gateway.Persistence
            .UninstallAsync(cancellationToken)
            .ConfigureAwait(false);
        ClawCtlConsole.WriteGatewayUninstalled(output, stop, removed);
        return removed.Succeeded ? 0 : 1;
    }

    private static async Task<int> RunGatewayDiagnoseAsync(
        Func<HostOptions, Action<string>, GatewayRuntime>? factory,
        HostOptions options,
        Action<string> log,
        TextWriter output,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode,
        CancellationToken cancellationToken)
    {
        // Diagnose is what a user runs when something is already wrong, so it
        // must survive the failures it exists to explain rather than refusing
        // to start.
        GatewayRuntime gateway;
        try
        {
            gateway = CreateGateway(factory, options, log);
        }
        catch (Exception exception) when (
            exception is SessionException or GatewayConfigurationException)
        {
            ClawCtlConsole.WriteGatewayUnavailable(output, exception.Message);
            return 1;
        }

        GatewayDiagnosticReport report = await GatewayDiagnostics
            .CollectAsync(
                gateway,
                gateway.Paths,
                options,
                gateway.Session.Coordinator,
                resolveNode ?? NodeRuntimeResolver.ResolveAsync,
                cancellationToken)
            .ConfigureAwait(false);
        ClawCtlConsole.WriteGatewayDiagnostics(output, report);
        return 0;
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

    private static async Task<int> RunSetupAsync(
        HostOptions options,
        Action<string> log,
        TextWriter output,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode,
        Func<HostOptions, Action<string>, GatewayRuntime>? createGatewayRuntime,
        CancellationToken cancellationToken)
    {
        NodeRuntime nodeRuntime = await (
            resolveNode ?? NodeRuntimeResolver.ResolveAsync)(
                cancellationToken).ConfigureAwait(false);
        ClawCtlConsole.WriteNodeRuntimeSummary(output, nodeRuntime);
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Confirmed the packaged OpenClaw application is present.");
        ClawCtlConsole.WritePackageSummary(output, applicationDirectory);

        GatewayRuntime gateway = CreateGateway(
            createGatewayRuntime,
            options,
            log);
        SessionRecord session = await gateway.Session.Coordinator
            .EnsureStartedAsync(cancellationToken)
            .ConfigureAwait(false);
        string stagedHelper = gateway.Session.StageHelper(session);
        log($"Staged the isolated-session helper at {stagedHelper}.");

        GatewayLaunchConfiguration launch = gateway.Configuration.Resolve(
            gateway.Paths.StateRoot,
            _ => null);
        gateway.Configuration.Write(launch);

        GatewayPersistenceInstallResult persistence = await gateway.Persistence
            .InstallAsync(cancellationToken)
            .ConfigureAwait(false);

        if (persistence.State is GatewayPersistenceState.Ready)
        {
            gateway.Session.SetupState.Write(new SetupRecord
            {
                ApplicationId = gateway.Session.ApplicationId,
                CompletedUtc = DateTimeOffset.UtcNow
            });
        }

        ClawCtlConsole.WriteSetupSummary(
            output,
            session,
            launch,
            persistence);
        await output.WriteLineAsync("  Gateway: not started.")
            .ConfigureAwait(false);
        return persistence.State is GatewayPersistenceState.Ready ? 0 : 1;
    }

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
