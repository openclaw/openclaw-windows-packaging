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
                    startup.ResolveNode).ConfigureAwait(false)
                : await RunAgentAsync(
                    options,
                    WriteDiagnostic,
                    startup.ResolveNode ?? (_ => Task.FromResult(NodeRuntimeResolver.Resolve(
                        GetPackagedNodeArchivePath(options)))),
                    startup.LaunchOpenClaw ?? GatewayLauncher.RunAsync,
                    startup.CreateSessionRuntime)
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
            resolveNode ?? (_ => Task.FromResult(NodeRuntimeResolver.Resolve(
                GetPackagedNodeArchivePath(options)))),
            GatewayLauncher.RunAsync).ConfigureAwait(false);

    // launchOpenClaw is a test seam: tests substitute a fake in place of
    // GatewayLauncher.RunAsync so they can assert launch behavior without
    // starting a real Node child process or Windows job object.
    internal static async Task<int> RunAgentAsync(
        HostOptions options,
        Action<string> log,
        Func<CancellationToken, Task<NodeRuntime>> resolveNode,
        LaunchOpenClawAsync launchOpenClaw,
        Func<Action<string>, Session.SessionRuntime>? createSessionRuntime = null,
        Func<bool>? isInteractive = null,
        Func<CancellationToken, Task<Mxc.MxcReadinessReport>>? probeReadiness = null,
        Func<string?>? getPackageFamilyName = null)
    {
        Session.SessionMode mode = Session.SessionRoutingPolicy.ReadMode(
            Environment.GetEnvironmentVariable);
        if (mode == Session.SessionMode.Disabled)
        {
            return await RunDirectAsync().ConfigureAwait(false);
        }

        Session.SessionRuntime? runtime = null;
        if (createSessionRuntime is null || probeReadiness is not null)
        {
            Mxc.MxcReadinessReport readiness = await (probeReadiness ??
                Mxc.MxcReadiness.ProbeAsync)(CancellationToken.None).ConfigureAwait(false);
            string? packageFamilyName =
                (getPackageFamilyName ?? (() => HostPaths.Create().PackageFamilyName))();
            Session.SessionRoutingDecision routing = Session.SessionRoutingPolicy.Decide(
                mode,
                packageFamilyName,
                readiness);
            log(routing.Reason);
            if (routing.Routing == Session.SessionRouting.Direct)
            {
                if (packageFamilyName is not null)
                {
                    runtime = (createSessionRuntime ?? Session.SessionRuntime.Create)(log);
                    runtime.ValidateSavedOwnershipForHostFallback();
                }

                return await RunDirectAsync().ConfigureAwait(false);
            }
        }

        Session.SessionRecord record;
        try
        {
            runtime ??= (createSessionRuntime ?? Session.SessionRuntime.Create)(log);
            record = await runtime.StartForExecutionAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Session.SessionCapabilityUnavailableException)
            when (mode == Session.SessionMode.Automatic)
        {
            log("Isolated session unavailable; running OpenClaw directly.");
            return await RunDirectAsync().ConfigureAwait(false);
        }

        string agentNodePath = runtime.RequireAgentNodePath(
            GetPackagedNodeArchivePath(options));
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Using the OpenClaw application directly from the package.");
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
                    Environment.GetEnvironmentVariable,
                    GatewayIsolationMode.Enabled)
            },
            CancellationToken.None).ConfigureAwait(false);

        async Task<int> RunDirectAsync()
        {
            NodeRuntime nodeRuntime = await resolveNode(CancellationToken.None)
                .ConfigureAwait(false);
            log(
                $"Using Node.js {nodeRuntime.Version} from " +
                $"{nodeRuntime.ExecutablePath}.");
            string directApplicationDirectory = GetPackagedApplicationDirectory(options);
            log("Using the OpenClaw application directly from the package.");
            return await launchOpenClaw(
                nodeRuntime.ExecutablePath,
                directApplicationDirectory,
                options.OpenClawArguments,
                GatewayIsolationMode.Disabled,
                CancellationToken.None,
                log).ConfigureAwait(false);
        }
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
        Func<Session.SessionRuntime>? createSessionRuntime = null,
        Func<CancellationToken, Task<Gateway.GatewayPersistenceInstallResult>>?
            installRecovery = null,
        Func<CancellationToken, Task<Gateway.GatewayPersistenceRemovalResult>>?
            removeRecovery = null)
    {
        Session.SessionRuntime? sessionRuntime = null;
        Session.SessionRuntime GetSessionRuntime() =>
            sessionRuntime ??= createSessionRuntime?.Invoke() ??
                Session.SessionRuntime.Create(log);

        RootCommand command = ClawCtlCommandLine.Create(
            new ClawCtlHandlers
            {
                Setup = async cancellationToken =>
                {
                    int result = await RunSetupAsync(
                        options, log, output, resolveNode, cancellationToken)
                        .ConfigureAwait(false);
                    if (result != 0)
                    {
                        return result;
                    }

                    if (Session.SessionRoutingPolicy.ReadMode(
                        Environment.GetEnvironmentVariable) == Session.SessionMode.Disabled)
                    {
                        await output.WriteLineAsync(
                            "Isolated session setup was skipped because OPENCLAW_SESSION is disabled.")
                            .ConfigureAwait(false);
                        return 0;
                    }

                    try
                    {
                        Session.SessionRuntime runtime = GetSessionRuntime();
                        using Session.ISessionLockHandle handle =
                            runtime.AcquireLifecycleLock();
                        runtime.SetupState.Write(new Session.SetupRecord
                        {
                            ApplicationId = runtime.ApplicationId,
                            Phase = Session.SetupPhase.Preparing
                        });
                        Session.SessionRecord record =
                            await runtime.Coordinator.EnsureStartedAsync(cancellationToken)
                                .ConfigureAwait(false);
                        string helperPath = runtime.StageHelper(record);
                        SessionRuntimeInstallResult agentRuntime =
                            await runtime.Executor.InstallRuntimeAsync(
                                record,
                                helperPath,
                                GetPackagedNodeArchivePath(options),
                                cancellationToken)
                            .ConfigureAwait(false);
                        Gateway.GatewayPersistenceInstallResult recovery =
                            installRecovery is null
                                ? await Gateway.GatewayRuntime.CreateRecoveryManager(log)
                                    .InstallAsync(cancellationToken)
                                    .ConfigureAwait(false)
                                : await installRecovery(cancellationToken)
                                    .ConfigureAwait(false);
                        await output.WriteLineAsync(recovery.Message).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(recovery.Detail))
                        {
                            await output.WriteLineAsync(recovery.Detail).ConfigureAwait(false);
                        }

                        if (recovery.State != Gateway.GatewayPersistenceState.Ready)
                        {
                            return 1;
                        }

                        runtime.CompleteSetup(record, agentRuntime, startupEnabled: true);
                        await output.WriteLineAsync("OpenClaw isolated session is ready.")
                            .ConfigureAwait(false);
                        return 0;
                    }
                    catch (Session.SessionException exception)
                    {
                        log($"Isolated session setup is unavailable: {exception.Message}");
                        await output.WriteLineAsync(
                            $"OpenClaw setup could not complete: {exception.Message}")
                            .ConfigureAwait(false);
                        return 1;
                    }
                },
                Status = async cancellationToken =>
                {
                    Session.SessionStatus status = GetSessionRuntime()
                        .Coordinator.GetRecordedStatus();
                    await output.WriteLineAsync(status.Availability.ToString())
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(status.Detail))
                    {
                        await output.WriteLineAsync(status.Detail)
                            .ConfigureAwait(false);
                    }
                    return 0;
                },
                Teardown = async (force, cancellationToken) =>
                {
                    if (!force)
                    {
                        await error.WriteLineAsync(
                            "Teardown removes the isolated session and its data. Re-run with --force to continue.")
                            .ConfigureAwait(false);
                        return 1;
                    }

                    Session.SessionRuntime runtime = GetSessionRuntime();
                    using Session.ISessionLockHandle handle =
                        runtime.AcquireLifecycleLock();
                    Gateway.GatewayPersistenceRemovalResult recoveryRemoval =
                        removeRecovery is null
                            ? await Gateway.GatewayRuntime
                                .CreateRecoveryManager(log)
                                .UninstallAsync(cancellationToken)
                                .ConfigureAwait(false)
                            : await removeRecovery(cancellationToken)
                                .ConfigureAwait(false);
                    if (!recoveryRemoval.Succeeded)
                    {
                        await error.WriteLineAsync(
                            $"OpenClaw recovery could not be removed: {recoveryRemoval.Detail}")
                            .ConfigureAwait(false);
                        return 1;
                    }

                    await runtime.Coordinator.RemoveAsync(cancellationToken)
                        .ConfigureAwait(false);
                    runtime.GatewayState.Clear();
                    await output.WriteLineAsync("OpenClaw isolated session was removed.")
                        .ConfigureAwait(false);
                    return 0;
                },
                PowerShell = cancellationToken => RunPowerShellAsync(
                    options,
                    GetSessionRuntime(),
                    output,
                    cancellationToken),
                GatewayStart = async cancellationToken =>
                {
                    Gateway.GatewayStartResult result = await Gateway.GatewayRuntime
                        .Create(options, log, resolveNode)
                        .Controller
                        .StartAsync(GetSessionRuntime().HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(result.Message).ConfigureAwait(false);
                    return result.State == Gateway.GatewayState.Running ? 0 : 1;
                },
                GatewayStatus = async cancellationToken =>
                {
                    Gateway.GatewayStatusReport result = await Gateway.GatewayRuntime
                        .Create(options, log, resolveNode)
                        .Controller
                        .GetStatusAsync(GetSessionRuntime().HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(result.Message).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(result.Detail))
                    {
                        await output.WriteLineAsync(result.Detail).ConfigureAwait(false);
                    }
                    return result.State is Gateway.GatewayState.Running or Gateway.GatewayState.NotStarted
                        ? 0 : 1;
                },
                GatewayStop = async cancellationToken =>
                {
                    Gateway.GatewayStopResult result = await Gateway.GatewayRuntime
                        .Create(options, log, resolveNode)
                        .Controller
                        .StopAsync(GetSessionRuntime().HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(result.Message).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(result.Detail))
                    {
                        await output.WriteLineAsync(result.Detail).ConfigureAwait(false);
                    }
                    return result.Succeeded ? 0 : 1;
                }
            });

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

    private static async Task<int> RunPowerShellAsync(
        HostOptions options,
        Session.SessionRuntime runtime,
        TextWriter output,
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

        await output.WriteLineAsync(
            $"Opening {shell.DisplayName} as {record.AgentUserName ?? "the agent account"} " +
            "in the isolated session.").ConfigureAwait(false);
        await output.WriteLineAsync(
            "`openclaw` and `node` are on PATH; `clawctl` is not, because it " +
            "manages this session from outside it.").ConfigureAwait(false);
        await output.WriteLineAsync("Exit the shell to return.").ConfigureAwait(false);

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
                        Environment.GetEnvironmentVariable,
                        GatewayIsolationMode.Enabled),
                    Session.AgentToolShim.BuildEnvironment(agentNodePath, applicationDirectory))
            },
            $"Opening {shell.DisplayName} in the isolated session.",
            shell.DisplayName,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunSetupAsync(
        HostOptions options,
        Action<string> log,
        TextWriter output,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode,
        CancellationToken cancellationToken)
    {
        NodeRuntime nodeRuntime;
        if (resolveNode is not null)
        {
            nodeRuntime = await resolveNode(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            nodeRuntime = NodeRuntimeInstaller.EnsureInstalled(
                GetPackagedNodeArchivePath(options),
                log);
        }
        ClawCtlConsole.WriteNodeRuntimeSummary(output, nodeRuntime);
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Confirmed the packaged OpenClaw application is present.");
        ClawCtlConsole.WriteReadinessSummary(output, applicationDirectory);
        return 0;
    }

    internal delegate Task<int> LaunchOpenClawAsync(
        string nodePath,
        string applicationDirectory,
        IReadOnlyList<string> openClawArguments,
        GatewayIsolationMode gatewayIsolationMode,
        CancellationToken cancellationToken,
        Action<string>? log);

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
