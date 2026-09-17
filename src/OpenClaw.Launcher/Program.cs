using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
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
            if (startup.Entrypoint == HostEntrypoint.Control &&
                ReferenceEquals(startup.Output, Console.Out) &&
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
                    readEnvironmentVariable: startup.ReadEnvironmentVariable)
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
                        candidate is "start" or "status" or "stop");
                return action is null ? argument : $"{argument} {action}";
            }

            if (argument is "setup" or "status" or "collect-logs" or "teardown" or "pwsh")
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
        Func<bool>? isInteractive = null)
    {
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Using the OpenClaw application directly from the package.");

        Mxc.MxcReadinessReport readiness = await (probeReadiness ??
            Mxc.MxcReadiness.ProbeAsync)(CancellationToken.None).ConfigureAwait(false);
        Session.SessionSupportPolicy.EnsureSupported(
            (getPackageFamilyName ?? (() => HostPaths.Create().PackageFamilyName))(),
            readiness);
        log("The isolated-session backend is available.");

        Session.SessionRuntime runtime = (createSessionRuntime ??
            Session.SessionRuntime.Create)(log);
        Session.SessionRecord record =
            await runtime.StartForExecutionAsync(CancellationToken.None)
                .ConfigureAwait(false);
        string agentNodePath = runtime.RequireAgentNodePath(
            GetPackagedNodeArchivePath(options));
        return await runtime.Executor.ExecuteAsync(
            record,
            new Session.SessionExecutionRequest(
                runtime.RequireStagedHelper(record),
                agentNodePath,
                applicationDirectory,
                options.OpenClawArguments,
                record.WorkspacePath!)
            {
                AdditionalEnvironment = OpenClawRuntimeEnvironment.Build(
                    (isInteractive ?? (() => WindowsHostConsole.Instance.IsInteractive))(),
                    readEnvironmentVariable ?? Environment.GetEnvironmentVariable)
            },
            CancellationToken.None).ConfigureAwait(false);
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
        Session.IInstallationLifecycle? installationLifecycle = null,
        Func<string, string?>? readEnvironmentVariable = null,
        ClawCtlOutputOptions? controlOutputOptions = null)
    {
        Session.IInstallationLifecycle lifecycle =
            installationLifecycle ?? Session.InstallationLifecycle.Production;
        ClawCtlOutputOptions outputOptions =
            controlOutputOptions ?? new ClawCtlOutputOptions();
        Session.SessionRuntime? sessionRuntime = null;
        Session.SessionRuntime GetSessionRuntime() =>
            sessionRuntime ??= lifecycle.CreateRuntime(log);

        // Colour is decided the same way for narration and for the result that
        // follows it, so a run cannot narrate in colour and then render plain.
        // The returned scope restores the console mode and must be held for as
        // long as anything is being written.
        (bool UseColor, IDisposable? Restore) PrepareColor()
        {
            IDisposable? restore = null;
            bool useColor = ClawCtlColorPolicy.PrepareOutput(
                outputOptions.NoColor,
                outputOptions.Json,
                ReferenceEquals(output, Console.Out),
                WindowsHostConsole.Instance.IsInteractive,
                Environment.GetEnvironmentVariable,
                () => WindowsHostConsole.Instance
                    .TryEnableVirtualTerminalProcessing(output, log, out restore));
            return (useColor, restore);
        }

        int WriteResult(IClawCtlResult result)
        {
            if (outputOptions.Json)
            {
                ClawCtlJson.WriteResult(output, result);
            }
            else
            {
                (bool useColor, IDisposable? restore) = PrepareColor();
                using (restore)
                {
                    ClawCtlConsole.WriteResult(output, result, useColor);
                }
            }
            return result.ExitCode;
        }

        async Task<int> RunSetupCommandAsync(
            SetupOptions setupOptions,
            CancellationToken cancellationToken)
        {
            try
            {
                (bool useColor, IDisposable? restore) = outputOptions.Json
                    ? (false, null)
                    : PrepareColor();
                SetupCommandResult result;
                using (restore)
                {
                    result = await ClawCtlConsole.NarrateAsync(
                        output,
                        useColor,
                        narrate: !outputOptions.Json,
                        new ClawCtlProgress("Checking isolated-session support."),
                        progress => RunSetupAsync(
                            setupOptions,
                            options,
                            GetSessionRuntime,
                            lifecycle,
                            log,
                            progress,
                            cancellationToken))
                        .ConfigureAwait(false);
                }

                return WriteResult(result);
            }
            catch (Session.SessionException exception) when (outputOptions.Json)
            {
                return WriteResult(new SetupCommandResult(
                    1,
                    GetPackagedApplicationDirectory(options),
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
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Session.SessionStatus status = await runtime
                        .Coordinator.ProbeRecordedStatusAsync(cancellationToken)
                        .ConfigureAwait(false);
                    Gateway.GatewayStatusReport gateway = await Gateway.GatewayRuntime
                        .Create(options, runtime.Paths, runtime, log)
                        .Controller
                        .GetStatusAsync(runtime.HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    Gateway.GatewayPersistenceStatus recovery = await lifecycle
                        .GetRecoveryStatusAsync(log, cancellationToken)
                        .ConfigureAwait(false);
                    Session.SetupStateResult setup =
                        runtime.SetupState.Read(runtime.ApplicationId);
                    return WriteResult(new StatusCommandResult(
                        status,
                        gateway,
                        recovery,
                        setup.Record?.AgentNodeVersion));
                },
                CollectLogs = async (requestedPath, cancellationToken) =>
                {
                    HostPaths paths = HostPaths.Create();
                    Gateway.DiagnosticsBundleResult result =
                        await Gateway.GatewayRuntime.Create(
                            options,
                            paths,
                            GetSessionRuntime(),
                            log)
                        .CollectLogsAsync(requestedPath, cancellationToken)
                        .ConfigureAwait(false);
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
                    Session.TeardownResult result = await lifecycle.TeardownAsync(
                        options, runtime, log, lockAlreadyHeld: false, cancellationToken)
                        .ConfigureAwait(false);
                    return WriteResult(new TeardownCommandResult(result));
                },
                PowerShell = cancellationToken => RunPowerShellAsync(
                    options,
                    GetSessionRuntime(),
                    cancellationToken),
                GatewayStart = async cancellationToken =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Gateway.GatewayController controller = Gateway.GatewayRuntime
                        .Create(options, runtime.Paths, runtime, log)
                        .Controller;

                    // Narration is human guidance, so it is off whenever the
                    // caller asked for a document: stdout carries exactly one
                    // JSON object.
                    (bool useColor, IDisposable? restore) = PrepareColor();
                    Gateway.GatewayStartResult result;
                    using (restore)
                    {
                        result = await ClawCtlConsole.NarrateAsync(
                            output,
                            useColor,
                            narrate: !outputOptions.Json,
                            Gateway.GatewayStartProgress.Initial,
                            progress => controller.StartAsync(
                                runtime.HelperPath, cancellationToken, progress))
                            .ConfigureAwait(false);
                    }

                    int? port = Gateway.GatewayAddress.ResolvePort(result.Record);
                    return WriteResult(new GatewayCommandResult(
                        "start",
                        result.State,
                        result.Message,
                        null,
                        result.State == Gateway.GatewayState.Running ? 0 : 1,
                        port));
                },
                GatewayStatus = async cancellationToken =>
                {
                    Gateway.GatewayRuntime runtime = Gateway.GatewayRuntime.Create(options, log);
                    Gateway.GatewayStatusReport result = await runtime.Controller
                        .GetStatusAsync(GetSessionRuntime().HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    int? port = result.Record?.ObservedPorts is { Count: 1 }
                        ? result.Record.ObservedPorts[0]
                        : null;
                    return WriteResult(new GatewayCommandResult(
                        "status",
                        result.State,
                        result.Message,
                        result.Detail,
                        result.State is Gateway.GatewayState.Running or
                            Gateway.GatewayState.NotStarted
                            ? 0
                            : 1,
                        port));
                },
                GatewayStop = async cancellationToken =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Gateway.GatewayStopResult result = await Gateway.GatewayRuntime
                        .Create(options, runtime.Paths, runtime, log)
                        .Controller
                        .StopAsync(runtime.HelperPath, cancellationToken)
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

    private static async Task<SetupCommandResult> RunSetupAsync(
        SetupOptions setupOptions,
        HostOptions options,
        Func<Session.SessionRuntime> getSessionRuntime,
        Session.IInstallationLifecycle lifecycle,
        Action<string> log,
        IProgress<ClawCtlProgress> progress,
        CancellationToken cancellationToken)
    {
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Confirmed the packaged OpenClaw application is present.");
        // Throws with the reason and its remediation when this machine cannot
        // host a session. There is no session-free setup to fall back to, so
        // nothing is reported as ready before this succeeds.
        await lifecycle.EnsureSessionSupportedAsync(cancellationToken)
            .ConfigureAwait(false);

        FreshSetupWarning? warning = null;
        bool localStateCleared = false;
        try
        {
            Session.SessionRuntime runtime = getSessionRuntime();
            if (setupOptions.Fresh)
            {
                progress.Report(new ClawCtlProgress("Inspecting the existing installation."));
                // Resolve every required package input before removing state.
                _ = lifecycle.ValidatePackageRuntime(options, runtime);

                using Session.ISessionLockHandle handle = lifecycle.AcquireLifecycleLock(runtime);
                FreshDiagnosticReport report = WriteFreshDiagnosticReport(runtime, log);
                Session.TeardownResult teardownResult;
                try
                {
                    progress.Report(new ClawCtlProgress("Removing existing OpenClaw resources."));
                    teardownResult = await lifecycle.TeardownAsync(
                        options, runtime, log, lockAlreadyHeld: true, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    setupOptions.Force &&
                    exception is Session.SessionException or Mxc.MxcException)
                {
                    teardownResult = new Session.TeardownResult(
                        false,
                        "Teardown failed before external cleanup could be confirmed.",
                        exception.Message);
                }
                if (!teardownResult.Succeeded)
                {
                    if (!setupOptions.Force)
                    {
                        return new SetupCommandResult(
                            1,
                            applicationDirectory,
                            null,
                            null,
                            false,
                            Error: teardownResult.Detail ?? teardownResult.Message,
                            Fresh: true);
                    }

                    warning = new FreshSetupWarning(
                        teardownResult.Message,
                        teardownResult.Detail);
                }

                Session.IInstallationStateCleaner cleaner = lifecycle.CreateStateCleaner(runtime);
                try
                {
                    progress.Report(new ClawCtlProgress("Clearing package-local state."));
                    cleaner.Clear();
                    localStateCleared = true;
                }
                catch (Exception cleanupException)
                {
                    try
                    {
                        RestoreFreshDiagnosticReport(report, log);
                    }
                    catch (Exception restoreException)
                    {
                        throw new Session.SessionException(
                            "Fresh setup local cleanup failed and the pre-reset " +
                            $"diagnostic report could not be restored. Cleanup: {cleanupException.Message} " +
                            $"Report: {restoreException.Message}",
                            new AggregateException(cleanupException, restoreException));
                    }

                    throw;
                }

                RestoreFreshDiagnosticReport(report, log);
                log("Fresh setup cleared package-owned local state.");
                if (!teardownResult.Succeeded)
                {
                    const string residualWarning =
                        "WARNING: Forced fresh setup did not prove a pristine machine because owned external cleanup remains unresolved. " +
                        "Review the pre-reset report for residual sandbox or gateway identifiers. " +
                        "A later setup reset cannot remove resources whose ownership record was cleared; " +
                        "remove them through the backend's administrative cleanup path before treating this machine as pristine.";
                    log(residualWarning);
                }

                return await RunSetupCoreAsync(
                    runtime,
                    options,
                    lifecycle,
                    applicationDirectory,
                    log,
                    lockAlreadyHeld: true,
                    warning,
                    localStateCleared,
                    fresh: true,
                    progress,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RunSetupCoreAsync(
                runtime,
                options,
                lifecycle,
                applicationDirectory,
                log,
                lockAlreadyHeld: false,
                warning,
                localStateCleared,
                fresh: false,
                progress,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Session.SessionException exception)
        {
            log($"Isolated session setup is unavailable: {exception.Message}");
            return new SetupCommandResult(
                1,
                applicationDirectory,
                null,
                null,
                false,
                warning,
                Error: exception.Message,
                Fresh: setupOptions.Fresh,
                LocalStateCleared: localStateCleared);
        }
        catch (IOException exception)
        {
            log($"Fresh setup local cleanup failed: {exception.Message}");
            return new SetupCommandResult(
                1,
                applicationDirectory,
                null,
                null,
                false,
                warning,
                Error: exception.Message,
                Fresh: setupOptions.Fresh,
                LocalStateCleared: localStateCleared);
        }
        catch (UnauthorizedAccessException exception)
        {
            log($"Fresh setup local cleanup was denied: {exception.Message}");
            return new SetupCommandResult(
                1,
                applicationDirectory,
                null,
                null,
                false,
                warning,
                Error: exception.Message,
                Fresh: setupOptions.Fresh,
                LocalStateCleared: localStateCleared);
        }
        catch (OperationCanceledException)
        {
            string retryCommand = setupOptions.Fresh
                ? "clawctl setup --fresh"
                : "clawctl setup";
            log($"Setup was cancelled before it completed. Retry `{retryCommand}`.");
            return new SetupCommandResult(
                1,
                applicationDirectory,
                null,
                null,
                false,
                warning,
                Error:
                    "Setup was cancelled and may be incomplete. " +
                    $"Run `{retryCommand}` to retry.",
                Fresh: setupOptions.Fresh,
                LocalStateCleared: localStateCleared);
        }
    }

    internal static async Task<SetupCommandResult> RunSetupCoreAsync(
        Session.SessionRuntime runtime,
        HostOptions options,
        Session.IInstallationLifecycle lifecycle,
        string applicationDirectory,
        Action<string> log,
        bool lockAlreadyHeld,
        FreshSetupWarning? warning,
        bool localStateCleared,
        bool fresh,
        IProgress<ClawCtlProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new ClawCtlProgress("Preparing the isolated session."));
        using Session.ISessionLockHandle? handle = lockAlreadyHeld
            ? null
            : runtime.AcquireLifecycleLock();
        runtime.SetupState.Write(new Session.SetupRecord
        {
            ApplicationId = runtime.ApplicationId,
            Phase = Session.SetupPhase.Preparing
        });
        Session.SessionStartResult session = await runtime.Coordinator
            .EnsureStartedWithResultAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<string> supersededSandboxIds = (session.Record.SupersededSandboxIds ?? [])
            .Append(session.Record.SupersededSandboxId)
            .Append(session.SupersededRecord?.SandboxId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .Where(id => !string.Equals(
                id,
                session.Record.SandboxId,
                StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
        foreach (string supersededSandboxId in supersededSandboxIds)
        {
            if (runtime.GatewayState.ClearForSupersededSession(supersededSandboxId))
            {
                log($"Removed the gateway record for superseded session '{supersededSandboxId}'.");
            }
        }

        Session.SessionRecord record = session.Record;
        string helperPath = runtime.StageHelper(record);
        progress.Report(new ClawCtlProgress(
            "Installing Node.js in the isolated session."));
        SessionRuntimeInstallResult agentRuntime = await runtime.Executor.InstallRuntimeAsync(
            record, helperPath, GetPackagedNodeArchivePath(options), cancellationToken).ConfigureAwait(false);
        progress.Report(new ClawCtlProgress("Enabling gateway startup at sign-in."));
        Gateway.GatewayPersistenceInstallResult recovery = await lifecycle
            .InstallRecoveryAsync(log, cancellationToken).ConfigureAwait(false);

        if (recovery.State != Gateway.GatewayPersistenceState.Ready)
        {
            return new SetupCommandResult(
                1,
                applicationDirectory,
                $"{agentRuntime.Version}",
                recovery,
                false,
                warning,
                SandboxId: record.SandboxId,
                Fresh: fresh,
                RuntimeLocation: SetupRuntimeLocation.IsolatedSession,
                LocalStateCleared: localStateCleared);
        }

        progress.Report(new ClawCtlProgress("Finalizing setup."));
        runtime.CompleteSetup(record, agentRuntime, startupEnabled: true);
        return new SetupCommandResult(
            0,
            applicationDirectory,
            $"{agentRuntime.Version}",
            recovery,
            true,
            warning,
            SandboxId: record.SandboxId,
            Fresh: fresh,
            RuntimeLocation: SetupRuntimeLocation.IsolatedSession,
            LocalStateCleared: localStateCleared);
    }

    private static FreshDiagnosticReport WriteFreshDiagnosticReport(
        Session.SessionRuntime runtime,
        Action<string> log)
    {
        Session.SessionStatus session = runtime.Coordinator.GetRecordedStatus();
        Gateway.GatewayStateResult gateway = runtime.GatewayState.Read();
        string path = runtime.Paths.PreResetReportPath;
        string directory = Path.GetDirectoryName(path)
            ?? throw new Session.SessionException(
                "The pre-reset diagnostic report path has no parent directory.");
        Directory.CreateDirectory(directory);
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path))
            : [];
        if (lines.Count > 0)
        {
            lines.Add(string.Empty);
        }

        lines.AddRange(
        [
            "--- pre-reset snapshot ---",
            $"timestampUtc={DateTimeOffset.UtcNow:O}",
            $"applicationId={runtime.ApplicationId}",
            "report=pre-reset diagnostic metadata; credentials and local file contents are excluded"
        ]);
        if (session.Record is not null)
        {
            lines.Add($"sessionSandboxId={session.Record.SandboxId}");
            lines.Add($"sessionAgentUserName={session.Record.AgentUserName ?? string.Empty}");
            lines.Add($"sessionAgentUserSid={session.Record.AgentUserSid ?? string.Empty}");
        }
        else
        {
            lines.Add($"sessionRecordFault={session.Fault?.ToString() ?? "none"}");
        }

        if (gateway.Record is not null)
        {
            lines.Add($"gatewaySandboxId={gateway.Record.SandboxId}");
            lines.Add($"gatewayProcessId={gateway.Record.ProcessId}");
            lines.Add($"gatewayLaunchPending={gateway.Record.LaunchPending}");
        }
        else
        {
            lines.Add($"gatewayRecordFault={gateway.Fault?.ToString() ?? "none"}");
        }
        lines.Add("--- end pre-reset snapshot ---");

        File.WriteAllLines(path, lines);
        log($"Captured redacted pre-reset diagnostic report at {path}.");
        return new FreshDiagnosticReport(path, lines);
    }

    private static void RestoreFreshDiagnosticReport(
        FreshDiagnosticReport report,
        Action<string> log)
    {
        string directory = Path.GetDirectoryName(report.Path)
            ?? throw new Session.SessionException(
                "The pre-reset diagnostic report path has no parent directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(report.Path, report.Lines);
        log($"Preserved the redacted pre-reset diagnostic report at {report.Path}.");
    }

    private sealed record FreshDiagnosticReport(
        string Path,
        IReadOnlyList<string> Lines);

    private static async Task<int> RunPowerShellAsync(
        HostOptions options,
        Session.SessionRuntime runtime,
        CancellationToken cancellationToken)
    {
        Session.SessionRecord record = runtime.RequireSetup();
        record = await runtime.Coordinator.StartRecordedAsync(cancellationToken)
            .ConfigureAwait(false);
        string helperPath = runtime.RequireStagedHelper(record);
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        string agentNodePath = runtime.RequireAgentNodePath(
            GetPackagedNodeArchivePath(options));
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
        Session.AgentShell shell = Session.AgentShellResolver.Resolve(File.Exists);

        return await runtime.Executor.ExecuteCommandAsync(
            record,
            new Session.SessionCommandRequest(
                helperPath,
                shell.ExecutablePath,
                Session.AgentShellResolver.BuildArguments(
                    record.WorkspacePath!,
                    record.AgentUserName ?? "agent",
                    tools.DirectoryPath,
                    nodeDirectory),
                record.WorkspacePath!)
            {
                AdditionalEnvironment = Session.SessionExecutor.MergeEnvironment(
                    OpenClawRuntimeEnvironment.Build(
                        WindowsHostConsole.Instance.IsInteractive,
                        Environment.GetEnvironmentVariable),
                    Session.AgentToolShim.BuildEnvironment(agentNodePath, applicationDirectory))
            },
            $"Opening {shell.DisplayName} in the isolated session.",
            shell.DisplayName,
            cancellationToken).ConfigureAwait(false);
    }

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

    private static string GetPackagedNodeArchivePath(HostOptions options)
    {
        string? archivePath = options.PackagedNodeArchivePath;
        string expectedPath = archivePath ?? Path.Combine(
            AppContext.BaseDirectory,
            "runtime");
        if (archivePath is null || !File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                "The packaged Node.js runtime archive was not found.",
                expectedPath);
        }

        return archivePath;
    }
}
