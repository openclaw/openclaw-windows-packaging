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
                    startup.ResolveNode,
                    startup.InstallationLifecycle).ConfigureAwait(false)
                : await RunAgentAsync(
                    options,
                    WriteDiagnostic,
                    startup.ResolveNode ?? (_ => Task.FromResult(NodeRuntimeResolver.Resolve(
                        GetPackagedNodeArchivePath(options)))),
                    startup.LaunchOpenClaw ?? GatewayLauncher.RunAsync,
                    startup.InstallationLifecycle is null
                        ? null
                        : startup.InstallationLifecycle.CreateRuntime)
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
        Func<CancellationToken, Task<Mxc.MxcReadinessReport>>? probeReadiness = null,
        Func<string?>? getPackageFamilyName = null,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Using the OpenClaw application directly from the package.");

        Session.SessionMode mode = Session.SessionRoutingPolicy.ReadMode(
            readEnvironmentVariable ?? Environment.GetEnvironmentVariable);
        Session.SessionRoutingDecision routing;
        if (mode == Session.SessionMode.Disabled)
        {
            routing = new Session.SessionRoutingDecision(
                Session.SessionRouting.Direct,
                $"{Session.SessionRoutingPolicy.ModeVariable} is set to 0.");
        }
        else
        {
            Mxc.MxcReadinessReport readiness = await (probeReadiness ??
                Mxc.MxcReadiness.ProbeAsync)(CancellationToken.None).ConfigureAwait(false);
            routing = Session.SessionRoutingPolicy.Decide(
                mode,
                (getPackageFamilyName ?? (() => HostPaths.Create().PackageFamilyName))(),
                readiness);
        }

        log(routing.Reason);
        if (routing.Routing == Session.SessionRouting.Session)
        {
            Session.SessionRuntime runtime = (createSessionRuntime ??
                Session.SessionRuntime.Create)(log);
            Session.SessionRecord record =
                await runtime.StartForExecutionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            Version packagedVersion = NodeRuntimeInstaller.GetArchiveVersion(
                GetPackagedNodeArchivePath(options),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            string agentNodePath = runtime.RequireAgentNodePath(packagedVersion);
            return await runtime.Executor.ExecuteAsync(
                record,
                new Session.SessionExecutionRequest(
                    runtime.RequireStagedHelper(record),
                    agentNodePath,
                    applicationDirectory,
                    options.OpenClawArguments,
                    Environment.CurrentDirectory),
                CancellationToken.None).ConfigureAwait(false);
        }

        NodeRuntime nodeRuntime = await resolveNode(CancellationToken.None)
            .ConfigureAwait(false);
        log(
            $"Using Node.js {nodeRuntime.Version} from " +
            $"{nodeRuntime.ExecutablePath}.");
        return await launchOpenClaw(
            nodeRuntime.ExecutablePath,
            applicationDirectory,
            options.OpenClawArguments,
            CancellationToken.None,
            log).ConfigureAwait(false);
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
        Session.IInstallationLifecycle? installationLifecycle = null)
    {
        _ = resolveNode;
        Session.IInstallationLifecycle lifecycle =
            installationLifecycle ?? Session.InstallationLifecycle.Production;
        Session.SessionRuntime? sessionRuntime = null;
        Session.SessionRuntime GetSessionRuntime() =>
            sessionRuntime ??= lifecycle.CreateRuntime(log);

        RootCommand command = ClawCtlCommandLine.Create(
            new ClawCtlHandlers
            {
                Setup = (setupOptions, cancellationToken) => RunSetupAsync(
                    setupOptions,
                    options,
                    GetSessionRuntime,
                    lifecycle,
                    log,
                    output,
                    cancellationToken),
                Status = async cancellationToken =>
                {
                    Session.SessionStatus status = await GetSessionRuntime()
                        .Coordinator.ProbeRecordedStatusAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(DescribeSessionStatus(status))
                        .ConfigureAwait(false);
                    return status.Availability is Session.SessionAvailability.Stale or
                        Session.SessionAvailability.BackendUnavailable or
                        Session.SessionAvailability.BackendError or
                        Session.SessionAvailability.Unusable
                        ? 1
                        : 0;
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
                    if (result.BundlePath is not null)
                    {
                        await output.WriteLineAsync(
                            $"Diagnostics bundle created: {result.BundlePath}").ConfigureAwait(false);
                    }
                    else
                    {
                        await output.WriteLineAsync(
                            "No diagnostic files were available to bundle.").ConfigureAwait(false);
                    }

                    foreach (string warning in result.Notes)
                    {
                        await output.WriteLineAsync($"Warning: {warning}").ConfigureAwait(false);
                    }

                    return 0;
                },
                Teardown = async (_, cancellationToken) =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Session.TeardownResult result = await lifecycle.TeardownAsync(
                        options, runtime, log, lockAlreadyHeld: false, cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(result.Message).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(result.Detail))
                    {
                        await output.WriteLineAsync(result.Detail).ConfigureAwait(false);
                    }

                    return result.Succeeded ? 0 : 1;
                },
                PowerShell = cancellationToken => RunPowerShellAsync(
                    options,
                    GetSessionRuntime(),
                    output,
                    cancellationToken),
                GatewayStart = async cancellationToken =>
                {
                    Gateway.GatewayStartResult result = await Gateway.GatewayRuntime
                        .Create(options, log)
                        .Controller
                        .StartAsync(GetSessionRuntime().HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(result.Message).ConfigureAwait(false);
                    return result.State == Gateway.GatewayState.Running ? 0 : 1;
                },
                GatewayStatus = async cancellationToken =>
                {
                    Gateway.GatewayStatusReport result = await Gateway.GatewayRuntime
                        .Create(options, log)
                        .Controller
                        .GetStatusAsync(GetSessionRuntime().HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(result.Message).ConfigureAwait(false);
                    return result.State is Gateway.GatewayState.Running or Gateway.GatewayState.NotStarted
                        ? 0 : 1;
                },
                GatewayStop = async cancellationToken =>
                {
                    Gateway.GatewayStopResult result = await Gateway.GatewayRuntime
                        .Create(options, log)
                        .Controller
                        .StopAsync(GetSessionRuntime().HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(result.Message).ConfigureAwait(false);
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

    private static async Task<int> RunSetupAsync(
        SetupOptions setupOptions,
        HostOptions options,
        Func<Session.SessionRuntime> getSessionRuntime,
        Session.IInstallationLifecycle lifecycle,
        Action<string> log,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Confirmed the packaged OpenClaw application is present.");
        ClawCtlConsole.WriteReadinessSummary(output, applicationDirectory);

        try
        {
            Session.SessionRuntime runtime = getSessionRuntime();
            if (setupOptions.Fresh)
            {
                // Resolve every required package input before removing state.
                _ = lifecycle.ValidatePackageRuntime(options, runtime);

                using Session.ISessionLockHandle handle = lifecycle.AcquireLifecycleLock(runtime);
                string reportPath = WriteFreshDiagnosticReport(runtime, log);
                await output.WriteLineAsync($"Pre-reset diagnostic report: {reportPath}")
                    .ConfigureAwait(false);
                Session.TeardownResult teardownResult = await lifecycle.TeardownAsync(
                    options, runtime, log, lockAlreadyHeld: true, cancellationToken)
                    .ConfigureAwait(false);
                if (!teardownResult.Succeeded)
                {
                    await output.WriteLineAsync(
                        $"Warning: Fresh setup stopped because teardown is incomplete: {teardownResult.Message}")
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(teardownResult.Detail))
                    {
                        await output.WriteLineAsync(teardownResult.Detail).ConfigureAwait(false);
                    }

                    return 1;
                }

                Session.IInstallationStateCleaner cleaner = lifecycle.CreateStateCleaner(runtime);
                cleaner.Clear();
                log("Fresh setup cleared package-owned local state.");
                return await RunSetupCoreAsync(
                    runtime, options, lifecycle, output, log, lockAlreadyHeld: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RunSetupCoreAsync(
                runtime, options, lifecycle, output, log, lockAlreadyHeld: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Session.SessionException exception)
        {
            log($"Isolated session setup is unavailable: {exception.Message}");
            await output.WriteLineAsync($"OpenClaw setup could not complete: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        catch (IOException exception)
        {
            log($"Fresh setup local cleanup failed: {exception.Message}");
            await output.WriteLineAsync($"OpenClaw setup could not complete: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            log($"Fresh setup local cleanup was denied: {exception.Message}");
            await output.WriteLineAsync($"OpenClaw setup could not complete: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<int> RunSetupCoreAsync(
        Session.SessionRuntime runtime,
        HostOptions options,
        Session.IInstallationLifecycle lifecycle,
        TextWriter output,
        Action<string> log,
        bool lockAlreadyHeld,
        CancellationToken cancellationToken)
    {
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
        if (session.SupersededRecord is not null &&
            runtime.GatewayState.ClearForSupersededSession(session.SupersededRecord.SandboxId))
        {
            log("Removed the gateway record for the superseded session.");
        }

        Session.SessionRecord record = session.Record;
        string helperPath = runtime.StageHelper(record);
        SessionRuntimeInstallResult agentRuntime = await runtime.Executor.InstallRuntimeAsync(
            record, helperPath, GetPackagedNodeArchivePath(options), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Node.js {agentRuntime.Version} is ready in the isolated agent session.")
            .ConfigureAwait(false);
        Gateway.GatewayPersistenceInstallResult recovery = await lifecycle
            .InstallRecoveryAsync(log, cancellationToken).ConfigureAwait(false);
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
        await output.WriteLineAsync("OpenClaw isolated session is ready.").ConfigureAwait(false);
        return 0;
    }

    private static string WriteFreshDiagnosticReport(
        Session.SessionRuntime runtime,
        Action<string> log)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenClawGatewayMSIX", "fresh-reset");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"pre-reset-{Guid.NewGuid():N}.log");
        File.WriteAllLines(path,
        [
            $"timestampUtc={DateTimeOffset.UtcNow:O}",
            $"applicationId={runtime.ApplicationId}",
            "report=pre-reset diagnostic metadata; credentials and local file contents are excluded"
        ]);
        log($"Captured redacted pre-reset diagnostic report at {path}.");
        return path;
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
        Version packagedVersion = NodeRuntimeInstaller.GetArchiveVersion(
            GetPackagedNodeArchivePath(options),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        string agentNodePath = runtime.RequireAgentNodePath(packagedVersion);
        string nodeDirectory = Path.GetDirectoryName(agentNodePath)
            ?? throw new Session.SessionException(
                "The agent's Node.js runtime has no parent directory.");
        Session.AgentTools tools = Session.AgentToolShim.Install(record.WorkspacePath!);
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
                        Environment.GetEnvironmentVariable),
                    Session.AgentToolShim.BuildEnvironment(agentNodePath, applicationDirectory))
            },
            $"Opening {shell.DisplayName} in the isolated session.",
            shell.DisplayName,
            cancellationToken).ConfigureAwait(false);
    }

    private static string DescribeSessionStatus(Session.SessionStatus status) =>
        status.Availability switch
        {
            Session.SessionAvailability.None =>
                "No isolated session is recorded. Run `clawctl setup` first.",
            Session.SessionAvailability.Running =>
                $"Isolated session is running: {status.Record!.SandboxId}.",
            Session.SessionAvailability.Stale =>
                $"Recorded isolated session is stale: {status.Record!.SandboxId}. {status.Detail}",
            Session.SessionAvailability.BackendUnavailable =>
                $"MXC backend is unavailable for recorded session {status.Record!.SandboxId}: {status.Detail}",
            Session.SessionAvailability.BackendError =>
                $"MXC could not verify recorded session {status.Record!.SandboxId}: {status.Detail}",
            Session.SessionAvailability.Unusable =>
                $"The isolated-session record is unusable: {status.Detail}",
            _ => $"Isolated session is recorded: {status.Record!.SandboxId}."
        };

    internal delegate Task<int> LaunchOpenClawAsync(
        string nodePath,
        string applicationDirectory,
        IReadOnlyList<string> openClawArguments,
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
