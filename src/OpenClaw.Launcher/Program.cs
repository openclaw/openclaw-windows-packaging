using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using OpenClaw.SessionProtocol;

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
        IDisposable? consoleRestore = null;
        var controlOutputOptions = new ClawCtlOutputOptions
        {
            Json = ResolveBooleanOption(args, "--json")
        };
        TextWriter output = startup.Output;
        TextWriter error = startup.Error;

        void WriteConsoleError(string message)
        {
            try
            {
                error.WriteLine(message);
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

        void WriteFailureOutput(Action write, string destination)
        {
            try
            {
                write();
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException)
            {
                WriteDiagnostic(
                    $"Failure output to {destination} failed: " +
                    $"{exception.GetType().Name}.");
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
            if (startup.UsesProcessConsoleWriters &&
                WindowsHostConsole.Instance.IsInteractive)
            {
                consoleRestore = WindowsHostConsole.Instance.Capture(WriteDiagnostic);
                WindowsHostConsole.Instance.InitializeUtf8();
                output = Console.Out;
                error = Console.Error;
            }

            HostOptions options = HostOptions.Parse(args, startup.BaseDirectory);
            return startup.Entrypoint == HostEntrypoint.Control
                ? await RunControlAsync(
                    options,
                    args,
                    WriteDiagnostic,
                    output,
                    error,
                    startup.InstallationLifecycle,
                    controlOutputOptions: controlOutputOptions).ConfigureAwait(false)
                : await RunAgentAsync(
                    options,
                    WriteDiagnostic,
                    startup.InstallationLifecycle is null
                        ? null
                        : startup.InstallationLifecycle.CreateRuntime,
                    probeReadiness: startup.ProbeReadiness,
                    getPackageFamilyName: startup.GetPackageFamilyName,
                    readEnvironmentVariable: startup.ReadEnvironmentVariable,
                    isInteractive: startup.IsInteractive,
                    error: error,
                    errorIsProcessConsoleWriter:
                        startup.UsesProcessConsoleWriters ? () => true : null,
                    installationLifecycle: startup.InstallationLifecycle)
                    .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteDiagnostic($"Unhandled failure: {GetDiagnosticFailure(exception)}");
            if (startup.Entrypoint == HostEntrypoint.Control)
            {
                string command = ResolveClawCtlCommand(args);
                if (controlOutputOptions.Json)
                {
                    WriteFailureOutput(
                        () => ClawCtlJson.WriteFailure(
                            output,
                            command,
                            exception.Message),
                        "standard output");
                }
                else
                {
                    WriteFailureOutput(() =>
                    {
                        bool outputIsProcessConsoleWriter =
                            ReferenceEquals(error, Console.Error);
                        bool consoleIsInteractive =
                            WindowsHostConsole.Instance.IsInteractiveOutput(error);
                        IDisposable? restore = null;
                        bool useColor = ClawCtlColorPolicy.PrepareOutput(
                            args.Contains("--no-color", StringComparer.Ordinal),
                            json: false,
                            outputIsProcessConsoleWriter,
                            consoleIsInteractive,
                            Environment.GetEnvironmentVariable,
                            () => WindowsHostConsole.Instance
                                .TryEnableVirtualTerminalProcessing(
                                    error,
                                    WriteDiagnostic,
                                    out restore));

                        using (restore)
                        {
                            ClawCtlConsole.WriteUnexpectedFailure(
                                error,
                                command,
                                exception.Message,
                                useColor);
                        }
                    }, "standard error");
                }
            }

            else
            {
                WriteConsoleError($"{commandName}: {exception.Message}");
                if (diagnostics is not null)
                {
                    WriteConsoleError(
                        $"{commandName}: See diagnostics: {diagnostics.Path}");
                }
            }
            return 1;
        }
        finally
        {
            WriteDiagnostic("Host exiting.");
            consoleRestore?.Dispose();
            diagnostics?.Dispose();
        }
    }

    private static string ResolveClawCtlCommand(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument == "gateway-service")
            {
                string? action = args
                    .Skip(index + 1)
                    .FirstOrDefault(candidate =>
                        candidate is "start" or "status" or "stop" or "restart");
                return action is null ? argument : $"{argument} {action}";
            }

            if (argument is "setup" or "status" or "collect-logs" or "teardown" or "open" or "pwsh")
            {
                return argument;
            }
        }

        return "command";
    }

    private static bool ResolveBooleanOption(string[] args, string option)
    {
        bool value = false;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals(option, StringComparison.Ordinal))
            {
                value = index + 1 >= args.Length ||
                    !bool.TryParse(args[index + 1], out bool explicitValue) ||
                    explicitValue;
                continue;
            }

            if (argument.StartsWith($"{option}=", StringComparison.Ordinal) ||
                argument.StartsWith($"{option}:", StringComparison.Ordinal))
            {
                int separator = option.Length;
                value = bool.TryParse(argument[(separator + 1)..], out bool explicitValue) &&
                    explicitValue;
            }
        }

        return value;
    }

    // Every collaborator after the readiness probe is a test seam: tests
    // substitute fakes so they can assert launch behavior without provisioning
    // a real isolated session.
    internal static async Task<int> RunAgentAsync(
        HostOptions options,
        Action<string> log,
        Func<Action<string>, Session.SessionRuntime>? createSessionRuntime = null,
        Func<CancellationToken, Task<Mxc.MxcReadinessReport>>? probeReadiness = null,
        Func<string?>? getPackageFamilyName = null,
        Func<string, string?>? readEnvironmentVariable = null,
        Func<bool>? isInteractive = null,
        TextWriter? error = null,
        Func<string>? getLogonSessionId = null,
        TimeProvider? clock = null,
        Func<bool>? errorIsProcessConsoleWriter = null,
        Func<bool>? errorIsInteractive = null,
        Func<bool>? supportsUnicode = null,
        Session.IInstallationLifecycle? installationLifecycle = null)
    {
        string applicationDirectory = options.RequirePackagedApplicationDirectory();
        log("Using the OpenClaw application directly from the package.");

        Mxc.MxcReadinessReport readiness = await (probeReadiness ??
            Mxc.MxcReadiness.ProbeAsync)(CancellationToken.None).ConfigureAwait(false);
        Session.SessionSupportPolicy.EnsureSupported(
            (getPackageFamilyName ?? (() => HostPaths.Create().PackageFamilyName))(),
            readiness);
        log("The isolated-session backend is available.");

        bool interactive =
            (isInteractive ?? (() => WindowsHostConsole.Instance.IsInteractive))();
        TextWriter errorWriter = error ?? Console.Error;
        Func<string, string?> environmentReader =
            readEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        Session.SessionRuntime runtime = (createSessionRuntime ??
            Session.SessionRuntime.Create)(log);
        await EnsureSetupForLaunchAsync(
            options,
            runtime,
            installationLifecycle,
            applicationDirectory,
            interactive,
            environmentReader,
            errorWriter,
            log,
            errorIsProcessConsoleWriter,
            errorIsInteractive).ConfigureAwait(false);
        Session.SessionRecord record =
            await runtime.StartForExecutionAsync(CancellationToken.None)
                .ConfigureAwait(false);
        string agentNodePath = runtime.RequireAgentNodePath(
            options.RequirePackagedNodeArchivePath());
        int exitCode = await runtime.Executor.ExecuteAsync(
            record,
            new Session.SessionExecutionRequest(
                runtime.RequireStagedHelper(record),
                agentNodePath,
                applicationDirectory,
                options.OpenClawArguments,
                record.WorkspacePath!)
            {
                AdditionalEnvironment = BuildRuntimeEnvironment(
                    runtime,
                    applicationDirectory,
                    interactive,
                    environmentReader),
                NodeArgumentsPrefix = BuildNativeRedirectNodeArguments(runtime),
                NativeRootPath = runtime.GetAgentNativeRoot()
            },
            CancellationToken.None).ConfigureAwait(false);
        return await Gateway.AgentPostflight.Create(
            options,
            runtime,
            record,
            interactive,
            environmentReader,
            log,
            clock,
            getLogonSessionId,
            errorIsProcessConsoleWriter,
            errorIsInteractive,
            supportsUnicode)
            .RunAsync(exitCode, interactive, errorWriter)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Provisions this installation, when it has never been set up, before an
    /// <c>openclaw</c> launch needs the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narration goes to standard error. Standard output belongs to the
    /// OpenClaw child, so piping <c>openclaw --version</c> must not pick up a
    /// setup message.
    /// </para>
    /// <para>
    /// Only an absent setup marker is provisioned. Anything else is left for
    /// <see cref="Session.SessionRuntime.RequireSetup"/> to report with the
    /// command that resolves it.
    /// </para>
    /// </remarks>
    private static async Task EnsureSetupForLaunchAsync(
        HostOptions options,
        Session.SessionRuntime runtime,
        Session.IInstallationLifecycle? installationLifecycle,
        string applicationDirectory,
        bool interactive,
        Func<string, string?> environmentReader,
        TextWriter errorWriter,
        Action<string> log,
        Func<bool>? errorIsProcessConsoleWriter,
        Func<bool>? errorIsInteractive)
    {
        if (installationLifecycle is null)
        {
            log("Automatic setup is unavailable: no installation lifecycle was supplied.");
            return;
        }

        if (!Gateway.AutoBehaviorPolicy.IsAutomaticSetupEnabled(environmentReader))
        {
            // The operator opted out, so an installation that was never set up
            // must fail exactly as it did before automatic setup existed.
            log(
                "Automatic setup is disabled by " +
                $"{OpenClawRuntimeEnvironment.AutoSetupVariable}.");
            return;
        }

        if (!Session.SetupOrchestrator.NeedsProvisioning(runtime))
        {
            return;
        }

        IDisposable? restore = null;
        bool useColor = ClawCtlColorPolicy.PrepareForegroundOutput(
            noColor: false,
            json: false,
            errorIsProcessConsoleWriter?.Invoke() ??
                ReferenceEquals(errorWriter, Console.Error),
            interactive,
            errorIsInteractive?.Invoke() ??
                WindowsHostConsole.Instance.IsInteractiveOutput(errorWriter),
            environmentReader,
            () => WindowsHostConsole.Instance.TryEnableVirtualTerminalProcessing(
                errorWriter,
                log,
                out restore));
        using (restore)
        {
            _ = await ClawCtlConsole.NarrateAsync(
                errorWriter,
                useColor,
                narrate: true,
                new ClawCtlProgress("Setting up OpenClaw for first use."),
                progress => Session.SetupOrchestrator.EnsureAsync(
                    options,
                    runtime,
                    installationLifecycle,
                    applicationDirectory,
                    log,
                    progress,
                    CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The environment for an OpenClaw launch, excluding the pieces the agent
    /// account owns.
    /// </summary>
    /// <remarks>
    /// Built in one place so foreground and gateway launch paths resolve native
    /// addons the same way. The agent shell carries equivalent values through
    /// its command shim because agent tooling may replace process environment
    /// values before invoking <c>openclaw</c>. The redirect's preload is not
    /// here: it belongs on the agent Node.js argument vector.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> BuildRuntimeEnvironment(
        Session.SessionRuntime runtime,
        string applicationDirectory,
        bool isInteractive,
        Func<string, string?> readEnvironmentVariable)
    {
        IReadOnlyDictionary<string, string> environment =
            OpenClawRuntimeEnvironment.Build(isInteractive, readEnvironmentVariable);
        if (runtime.GetAgentNativeRoot() is not { Length: > 0 } nativeRoot)
        {
            return environment;
        }

        return Session.SessionExecutor.MergeEnvironment(
            environment,
            OpenClawRuntimeEnvironment.BuildNativeRedirect(
                applicationDirectory,
                nativeRoot));
    }

    /// <summary>
    /// The Node.js arguments that load the redirect, or <see langword="null"/>
    /// when setup staged nothing to redirect to.
    /// </summary>
    private static IReadOnlyList<string>? BuildNativeRedirectNodeArguments(
        Session.SessionRuntime runtime) =>
        runtime.GetAgentNativeRoot() is { Length: > 0 }
            ? OpenClawRuntimeEnvironment.BuildNativeRedirectNodeArguments(
                ResolveNativeRedirectPreloadPath())
            : null;

    internal static string ResolveNativeRedirectPreloadPath() =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                OpenClawRuntimeEnvironment.NodeScriptDirectoryName,
                OpenClawRuntimeEnvironment.NativeRedirectFileName));

    // output and error are required parameters (not Console defaults) so tests
    // can capture clawctl output without mutating global console state,
    // which would be unsafe across parallel test runs.
    internal static async Task<int> RunControlAsync(
        HostOptions options,
        IReadOnlyList<string> args,
        Action<string> log,
        TextWriter output,
        TextWriter error,
        Session.IInstallationLifecycle? installationLifecycle = null,
        Func<string, string?>? readEnvironmentVariable = null,
        ClawCtlOutputOptions? controlOutputOptions = null,
        Func<string>? getLogonSessionId = null,
        TimeProvider? clock = null,
        Func<string, Task>? launchBrowserAsync = null,
        Action? beforeBrowserValidation = null)
    {
        Session.IInstallationLifecycle lifecycle =
            installationLifecycle ?? Session.InstallationLifecycle.Production;
        ClawCtlOutputOptions outputOptions =
            controlOutputOptions ?? new ClawCtlOutputOptions();
        Session.SessionRuntime? sessionRuntime = null;
        Session.SessionRuntime GetSessionRuntime() =>
            sessionRuntime ??= lifecycle.CreateRuntime(log);

        // Narration goes to standard error and the result to standard output,
        // so colour is decided for the stream being written: redirecting one
        // must not strip or force colour on the other. The returned scope
        // restores the console mode and must be held for as long as that
        // stream is being written.
        (bool UseColor, IDisposable? Restore) PrepareColor(TextWriter target)
        {
            IDisposable? restore = null;
            bool useColor = ClawCtlColorPolicy.PrepareOutput(
                outputOptions.NoColor,
                outputOptions.Json,
                ReferenceEquals(target, Console.Out) ||
                    ReferenceEquals(target, Console.Error),
                WindowsHostConsole.Instance.IsInteractiveOutput(target),
                Environment.GetEnvironmentVariable,
                () => WindowsHostConsole.Instance
                    .TryEnableVirtualTerminalProcessing(target, log, out restore));
            return (useColor, restore);
        }

        async Task<T> NarrateOperationAsync<T>(
            ClawCtlProgress initial,
            Func<IProgress<ClawCtlProgress>, Task<T>> operation)
        {
            (bool useColor, IDisposable? restore) = outputOptions.Json
                ? (false, null)
                : PrepareColor(error);
            using (restore)
            {
                return await ClawCtlConsole.NarrateAsync(
                    error,
                    useColor,
                    narrate: !outputOptions.Json,
                    initial,
                    operation).ConfigureAwait(false);
            }
        }

        int WriteResult(IClawCtlResult result)
        {
            if (outputOptions.Json)
            {
                ClawCtlJson.WriteResult(output, result);
            }
            else
            {
                (bool useColor, IDisposable? restore) = PrepareColor(output);
                using (restore)
                {
                    ClawCtlConsole.WriteResult(output, result, useColor);
                }
            }
            return result.ExitCode;
        }

        void AcknowledgeManualGatewayStart(Session.SessionRuntime runtime)
        {
            new Gateway.AgentGatewayGuidance(
                runtime.LifecycleLock,
                _ => throw new InvalidOperationException(
                    "Manual acknowledgement must not check config readiness."),
                _ => throw new InvalidOperationException(
                    "Manual acknowledgement must not inspect the gateway."),
                new Gateway.GatewayGuidanceStateStore(
                    runtime.Paths.GatewayGuidanceStatePath),
                getLogonSessionId ?? Gateway.WindowsLogonSession.GetCurrentId,
                log,
                clock)
                .AcknowledgeManualStart();
        }

        int WriteGatewayStartResult(string action, Gateway.GatewayStartResult result)
        {
            int? port = Gateway.GatewayAddress.ResolvePort(result.Record);
            return WriteResult(new GatewayCommandResult(
                action,
                result.State,
                result.Message,
                null,
                result.State == Gateway.GatewayState.Running ? 0 : 1,
                port));
        }

        async Task<int> RunSetupCommandAsync(
            SetupOptions setupOptions,
            CancellationToken cancellationToken)
        {
            try
            {
                SetupCommandResult result = await NarrateOperationAsync(
                    new ClawCtlProgress("Checking isolated-session support."),
                    progress => Session.SetupOrchestrator.RunAsync(
                        setupOptions,
                        options,
                        GetSessionRuntime,
                        lifecycle,
                        log,
                        progress,
                        cancellationToken))
                    .ConfigureAwait(false);

                return WriteResult(result);
            }
            catch (Session.SessionException exception) when (outputOptions.Json)
            {
                return WriteResult(new SetupCommandResult(
                    1,
                    options.RequirePackagedApplicationDirectory(),
                    null,
                    null,
                    false,
                    Error: exception.Message,
                    Fresh: setupOptions.Fresh));
            }
        }

        RootCommand command = ClawCtlCommandLine.Create(
            new ClawCtlHandlers
            {
                Setup = RunSetupCommandAsync,
                Status = async cancellationToken =>
                {
                    StatusCommandResult result = await NarrateOperationAsync(
                        new ClawCtlProgress("Checking session, gateway, and recovery status."),
                        async _ =>
                        {
                            Session.SessionRuntime runtime = GetSessionRuntime();
                            Session.SessionStatus status = await runtime
                                .Coordinator.ProbeRecordedStatusAsync(cancellationToken)
                                .ConfigureAwait(false);
                            Gateway.GatewayStatusReport gateway =
                                await Gateway.GatewayRuntime
                                    .Create(options, runtime.Paths, runtime, log)
                                    .Controller
                                    .GetStatusAsync(runtime.HelperPath, cancellationToken)
                                    .ConfigureAwait(false);
                            Gateway.GatewayPersistenceStatus recovery = await lifecycle
                                .GetRecoveryStatusAsync(log, cancellationToken)
                                .ConfigureAwait(false);
                            Session.AgentConfigReadinessStatus? configReadiness =
                                gateway.State == Gateway.GatewayState.Running
                                    ? null
                                    : await Session.AgentConfigReadinessProbe.CheckAsync(
                                        runtime,
                                        status,
                                        cancellationToken).ConfigureAwait(false);
                            Session.SetupStateResult setup =
                                runtime.SetupState.Read(runtime.ApplicationId);
                            return new StatusCommandResult(
                                status,
                                gateway,
                                recovery,
                                setup.Record?.AgentNodeVersion,
                                configReadiness);
                        }).ConfigureAwait(false);
                    return WriteResult(result);
                },
                CollectLogs = async (requestedPath, cancellationToken) =>
                {
                    Gateway.DiagnosticsBundleResult result = await NarrateOperationAsync(
                        new ClawCtlProgress("Collecting redacted diagnostics."),
                        async _ =>
                        {
                            HostPaths paths = HostPaths.Create();
                            return await Gateway.GatewayRuntime.Create(
                                    options,
                                    paths,
                                    GetSessionRuntime(),
                                    log)
                                .CollectLogsAsync(requestedPath, cancellationToken)
                                .ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    return WriteResult(new CollectLogsCommandResult(result));
                },
                Teardown = async (force, cancellationToken) =>
                {
                    if (!force)
                    {
                        const string message =
                            "Teardown removes the isolated session and its data. " +
                            "Re-run with --force to continue.";
                        if (outputOptions.Json)
                        {
                            return WriteResult(new TeardownCommandResult(
                                new Session.TeardownResult(false, message)));
                        }

                        await error.WriteLineAsync(message).ConfigureAwait(false);
                        return 1;
                    }

                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Session.TeardownResult result = await NarrateOperationAsync(
                        new ClawCtlProgress("Removing the isolated session and its data."),
                        _ => lifecycle.TeardownAsync(
                            options,
                            runtime,
                            log,
                            lockAlreadyHeld: false,
                            cancellationToken)).ConfigureAwait(false);
                    return WriteResult(new TeardownCommandResult(result));
                },
                Open = async cancellationToken =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    try
                    {
                        _ = runtime.RequireSetup();
                    }
                    catch (Session.SessionException exception)
                    {
                        return WriteResult(new OpenCommandResult(null, exception.Message, 1));
                    }

                    OpenCommandResult result = await NarrateOperationAsync(
                        new ClawCtlProgress("Preparing the Control UI handoff."),
                        async _ =>
                    {
                        Gateway.GatewayStatusReport gateway = await Gateway.GatewayRuntime
                            .Create(options, runtime.Paths, runtime, log)
                            .Controller
                            .GetStatusAsync(runtime.HelperPath, cancellationToken)
                            .ConfigureAwait(false);
                        if (gateway.State != Gateway.GatewayState.Running)
                        {
                            string message = gateway.State is
                                Gateway.GatewayState.NotStarted or
                                Gateway.GatewayState.Stopped
                                ? $"{gateway.Message} Run `clawctl gateway-service start` before opening the Control UI."
                                : gateway.Message;
                            return new OpenCommandResult(gateway.State, message, 1);
                        }

                        Session.SessionRecord record =
                            await runtime.StartForExecutionAsync(cancellationToken)
                                .ConfigureAwait(false);
                        string applicationDirectory =
                            options.RequirePackagedApplicationDirectory();
                        string nodePath = runtime.RequireAgentNodePath(
                            options.RequirePackagedNodeArchivePath());
                        IReadOnlyDictionary<string, string> dashboardEnvironment =
                            BuildRuntimeEnvironment(
                                runtime,
                                applicationDirectory,
                                isInteractive: false,
                                readEnvironmentVariable ??
                                    Environment.GetEnvironmentVariable);
                        if (gateway.Record?.ObservedPorts is { Count: 1 } observedPorts)
                        {
                            dashboardEnvironment = Session.SessionExecutor.MergeEnvironment(
                                dashboardEnvironment,
                                new Dictionary<string, string>
                                {
                                    [Gateway.GatewayConfigurationStore.PortVariable] =
                                        observedPorts[0].ToString(
                                            CultureInfo.InvariantCulture)
                                });
                        }

                        Session.SessionCommandCaptureResult capture = await runtime.Executor
                            .ExecuteCommandCaptureAsync(
                                record,
                                new Session.SessionCommandRequest(
                                    runtime.RequireStagedHelper(record),
                                    nodePath,
                                    [
                                        .. BuildNativeRedirectNodeArguments(runtime) ?? [],
                                        Path.Combine(applicationDirectory, "openclaw.mjs"),
                                        "dashboard",
                                        "--json"
                                    ],
                                    record.WorkspacePath!)
                                {
                                    PathPrefix = Path.GetDirectoryName(nodePath),
                                    AdditionalEnvironment = dashboardEnvironment,
                                    NativeRootPath = runtime.GetAgentNativeRoot()
                                },
                                "Resolving the Control UI handoff in the isolated session.",
                                "OpenClaw dashboard",
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!Gateway.ControlUiHandoffParser.TryParse(
                                capture.StandardOutput,
                                gateway.Record?.ObservedPorts ?? [],
                                out string? browserUrl) ||
                            browserUrl is null)
                        {
                            return new OpenCommandResult(
                                gateway.State,
                                "OpenClaw did not return a usable Control UI handoff.",
                                1);
                        }

                        beforeBrowserValidation?.Invoke();
                        if (!runtime.IsCurrentSessionRecord(record))
                        {
                            return new OpenCommandResult(
                                gateway.State,
                                "The isolated session changed before the Control UI could be opened. Retry the command.",
                                1);
                        }

                        try
                        {
                            await (launchBrowserAsync ??
                                (url => LaunchBrowserAsync(url)))(browserUrl)
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception) when (
                            exception is System.ComponentModel.Win32Exception or
                            InvalidOperationException or
                            NotSupportedException)
                        {
                            log($"Browser launch failed: {exception.GetType().Name}");
                            return new OpenCommandResult(
                                gateway.State,
                                "The Control UI is ready, but the default browser could not be opened.",
                                1);
                        }

                        return new OpenCommandResult(
                            gateway.State,
                            "Opened the Control UI in the default browser.",
                            0);
                    }).ConfigureAwait(false);
                    return WriteResult(result);
                },
                PowerShell = (powerShellOptions, cancellationToken) => RunPowerShellAsync(
                    options,
                    GetSessionRuntime(),
                    powerShellOptions,
                    cancellationToken),
                Completion = (completionOptions, _) =>
                {
                    if (completionOptions.Uninstall)
                    {
                        string uninstalledProfilePath = PowerShellCompletion.Uninstall(
                            completionOptions.ProfilePath ??
                            PowerShellCompletion.DefaultProfilePath());
                        DeleteCompletionCache(GetSessionRuntime().Paths.CompletionCachePath);
                        return Task.FromResult(WriteResult(new CompletionCommandResult(
                            PowerShellCompletion.ClawCtlScript,
                            uninstalledProfilePath,
                            CachePath: null,
                            ExitCode: 0)));
                    }

                    string applicationDirectory =
                        options.RequirePackagedApplicationDirectory();
                    string openClawScript =
                        PowerShellCompletion.ReadPackagedOpenClawScript(applicationDirectory);
                    string combinedScript = PowerShellCompletion.BuildScript(openClawScript);
                    if (!completionOptions.Install)
                    {
                        return Task.FromResult(WriteResult(new CompletionCommandResult(
                            combinedScript,
                            ProfilePath: null,
                            CachePath: null,
                            ExitCode: 0)));
                    }

                    string profilePath = completionOptions.ProfilePath ??
                        PowerShellCompletion.DefaultProfilePath();
                    profilePath = PowerShellCompletion.Install(profilePath);
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    PowerShellCompletion.WriteScriptAtomically(
                        runtime.Paths.CompletionCachePath,
                        openClawScript);
                    return Task.FromResult(WriteResult(new CompletionCommandResult(
                        combinedScript,
                        profilePath,
                        runtime.Paths.CompletionCachePath,
                        ExitCode: 0)));
                },
                GatewayStart = async (recovery, cancellationToken) =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    bool retainedRecoveryInvocation =
                        !recovery &&
                        runtime.Paths.PackageFamilyName is string packageFamilyName &&
                        Gateway.GatewayLauncherScript.UpgradeLegacyActivationScript(
                            Path.ChangeExtension(
                                runtime.Paths.GatewayLauncherPath,
                                ".ps1"),
                            packageFamilyName,
                            log);
                    Gateway.GatewayController controller = Gateway.GatewayRuntime
                        .Create(options, runtime.Paths, runtime, log, clock)
                        .Controller;
                    if (!recovery && !retainedRecoveryInvocation)
                    {
                        AcknowledgeManualGatewayStart(runtime);
                    }

                    // Narration is human guidance, so it is off whenever the
                    // caller asked for a document: stdout carries exactly one
                    // JSON object.
                    Gateway.GatewayStartResult result = await NarrateOperationAsync(
                        Gateway.GatewayStartProgress.Initial,
                        progress => controller.StartAsync(
                            runtime.HelperPath, cancellationToken, progress))
                        .ConfigureAwait(false);

                    return WriteGatewayStartResult("start", result);
                },
                GatewayStatus = async cancellationToken =>
                {
                    GatewayCommandResult commandResult = await NarrateOperationAsync(
                        new ClawCtlProgress("Checking the gateway service."),
                        async _ =>
                        {
                            Session.SessionRuntime sessionRuntime = GetSessionRuntime();
                            Gateway.GatewayRuntime runtime = Gateway.GatewayRuntime.Create(
                                options,
                                sessionRuntime.Paths,
                                sessionRuntime,
                                log);
                            Gateway.GatewayStatusReport result = await runtime.Controller
                                .GetStatusAsync(
                                    sessionRuntime.HelperPath,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            Session.AgentConfigReadinessStatus? configReadiness = null;
                            if (result.State != Gateway.GatewayState.Running)
                            {
                                Session.SessionStatus session =
                                    await sessionRuntime.Coordinator
                                        .ProbeRecordedStatusAsync(cancellationToken)
                                        .ConfigureAwait(false);
                                configReadiness =
                                    await Session.AgentConfigReadinessProbe.CheckAsync(
                                        sessionRuntime,
                                        session,
                                        cancellationToken).ConfigureAwait(false);
                            }

                            int? port = result.Record?.ObservedPorts is { Count: 1 }
                                ? result.Record.ObservedPorts[0]
                                : null;
                            return new GatewayCommandResult(
                                "status",
                                result.State,
                                result.Message,
                                result.Detail,
                                result.State is Gateway.GatewayState.Running or
                                    Gateway.GatewayState.NotStarted
                                    ? configReadiness?.ProbeFailed == true ? 1 : 0
                                    : 1,
                                port,
                                Readiness: configReadiness);
                        }).ConfigureAwait(false);
                    return WriteResult(commandResult);
                },
                GatewayStop = async cancellationToken =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Gateway.GatewayStopResult result = await NarrateOperationAsync(
                        new ClawCtlProgress("Stopping the gateway service."),
                        _ => Gateway.GatewayRuntime
                            .Create(options, runtime.Paths, runtime, log)
                            .Controller
                            .StopAsync(runtime.HelperPath, cancellationToken))
                        .ConfigureAwait(false);
                    return WriteResult(new GatewayCommandResult(
                        "stop",
                        result.Stopped
                            ? Gateway.GatewayState.Stopped
                            : result.Succeeded
                                ? Gateway.GatewayState.NotStarted
                                : Gateway.GatewayState.Unknown,
                        result.Message,
                        result.Detail,
                        result.Succeeded ? 0 : 1));
                },
                GatewayRestart = async cancellationToken =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    AcknowledgeManualGatewayStart(runtime);
                    Gateway.GatewayController controller = Gateway.GatewayRuntime
                        .Create(options, runtime.Paths, runtime, log, clock)
                        .Controller;
                    Gateway.GatewayRestartResult result = await NarrateOperationAsync(
                        Gateway.GatewayStartProgress.StoppingFirst,
                        progress => controller.RestartAsync(
                            runtime.HelperPath,
                            cancellationToken,
                            progress))
                        .ConfigureAwait(false);

                    if (result.Start is null)
                    {
                        return WriteResult(new GatewayCommandResult(
                            "restart",
                            Gateway.GatewayState.Unknown,
                            result.Stop.Message,
                            result.Stop.Detail,
                            1));
                    }

                    return WriteGatewayStartResult("restart", result.Start);
                },
            },
            outputOptions);

        InvocationConfiguration configuration = new()
        {
            Output = output,
            Error = error,

            // Operational failures stay the host's responsibility. The default
            // handler would print its own message and return its own exit code,
            // losing the diagnostic log entry and the log path Main reports.
            EnableDefaultExceptionHandler = false,

            // Node lifetime is owned by the job object in the session host. The
            // library's termination timeout would add a second, conflicting
            // forced-exit policy and process-wide signal handlers.
            ProcessTerminationTimeout = null
        };

        return await command
            .Parse(args, ClawCtlCommandLine.CreateParserConfiguration())
            .InvokeAsync(configuration)
            .ConfigureAwait(false);
    }

    internal static Task LaunchBrowserAsync(
        string browserUrl,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        // Shell activation returns null when an already-running browser handles the URL.
        _ = (startProcess ?? Process.Start)(new ProcessStartInfo(browserUrl)
        {
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }

    private static async Task<int> RunPowerShellAsync(
        HostOptions options,
        Session.SessionRuntime runtime,
        PowerShellOptions powerShellOptions,
        CancellationToken cancellationToken)
    {
        Session.SessionRecord record = runtime.RequireSetup();
        record = await runtime.StartForExecutionAsync(cancellationToken)
            .ConfigureAwait(false);
        string helperPath = runtime.RequireStagedHelper(record);
        string applicationDirectory = options.RequirePackagedApplicationDirectory();
        string agentNodePath = runtime.RequireAgentNodePath(
            options.RequirePackagedNodeArchivePath());
        string nodeDirectory = Path.GetDirectoryName(agentNodePath)
            ?? throw new Session.SessionException(
                "The agent's Node.js runtime has no parent directory.");
        SessionToolInstallResult installedTools = await runtime.Executor.InstallToolsAsync(
            record,
            helperPath,
            cancellationToken).ConfigureAwait(false);
        Session.AgentTools tools = new(
            Path.GetDirectoryName(installedTools.ShimPath)
                ?? throw new Session.SessionException(
                    "The installed agent command shim has no parent directory."),
            installedTools.ShimPath!);
        bool interactive = powerShellOptions.Command is null && powerShellOptions.File is null;
        string? completionScriptPath = null;
        if (interactive && File.Exists(runtime.Paths.CompletionCachePath))
        {
            PowerShellCompletion.SynchronizeCacheIfInstalled(
                runtime.Paths.CompletionCachePath,
                PowerShellCompletion.ReadPackagedOpenClawScript(applicationDirectory));
        }
        if (interactive)
        {
            using Session.SessionWorkspaceOperation operation =
                runtime.Executor.CreateWorkspaceOperation(record);
            completionScriptPath = Session.SessionCompletionProjection.Project(
                operation, runtime.Paths.CompletionCachePath);
        }
        Session.AgentShell shell = Session.AgentShellResolver.Resolve(File.Exists);
        string? nativeRootPath = runtime.GetAgentNativeRoot();
        string? nativePreloadUrl = nativeRootPath is { Length: > 0 }
            ? new Uri(ResolveNativeRedirectPreloadPath()).AbsoluteUri
            : null;

        return await runtime.Executor.ExecuteCommandAsync(
            record,
            new Session.SessionCommandRequest(
                helperPath,
                shell.ExecutablePath,
                BuildPowerShellArguments(
                    powerShellOptions,
                    record,
                    completionScriptPath),
                record.WorkspacePath!)
            {
                PathPrefix = string.Join(
                    Path.PathSeparator,
                    tools.DirectoryPath,
                    nodeDirectory),
                AdditionalEnvironment = Session.SessionExecutor.MergeEnvironment(
                    OpenClawRuntimeEnvironment.Build(
                        WindowsHostConsole.Instance.IsInteractive,
                        Environment.GetEnvironmentVariable),
                    Session.AgentToolShim.BuildEnvironment(
                        agentNodePath,
                        applicationDirectory,
                        nativeRootPath,
                        nativePreloadUrl)),
                NativeRootPath = nativeRootPath
            },
            $"Opening {shell.DisplayName} in the isolated session.",
            shell.DisplayName,
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> BuildPowerShellArguments(
        PowerShellOptions options,
        Session.SessionRecord record,
        string? completionScriptPath)
    {
        if (options.Command is not null)
        {
            return Session.AgentShellResolver.BuildCommandArguments(options.Command);
        }

        if (options.File is not null)
        {
            return Session.AgentShellResolver.BuildFileArguments(
                options.File,
                options.Arguments);
        }

        return Session.AgentShellResolver.BuildInteractiveArguments(
            record.WorkspacePath!,
            record.AgentUserName ?? "agent",
            completionScriptPath);
    }

    private static void DeleteCompletionCache(string cachePath)
    {
        if (File.Exists(cachePath))
        {
            File.Delete(cachePath);
        }
    }
}
