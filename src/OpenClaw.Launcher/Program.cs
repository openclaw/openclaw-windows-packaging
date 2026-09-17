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
                    startup.InstallationLifecycle,
                    startup.ReadEnvironmentVariable).ConfigureAwait(false)
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
        Func<string, string?>? readEnvironmentVariable = null)
    {
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
                    Gateway.GatewayRuntime runtime = Gateway.GatewayRuntime.Create(options, log);
                    Gateway.GatewayStatusReport result = await runtime.Controller
                        .GetStatusAsync(GetSessionRuntime().HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await Gateway.GatewayControlOutput.WriteStatusAsync(
                        output,
                        result,
                        runtime.GetRecordedWorkspacePath(),
                        cancellationToken).ConfigureAwait(false);
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
                    await Gateway.GatewayControlOutput.WriteStopAsync(output, result)
                        .ConfigureAwait(false);
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

        // Throws with the reason and its remediation when this machine cannot
        // host a session. There is no session-free setup to fall back to, so
        // nothing is reported as ready before this succeeds.
        await lifecycle.EnsureSessionSupportedAsync(cancellationToken)
            .ConfigureAwait(false);

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
                Session.TeardownResult teardownResult;
                try
                {
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
                        await output.WriteLineAsync(
                            $"Warning: Fresh setup stopped because teardown is incomplete: {teardownResult.Message}")
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(teardownResult.Detail))
                        {
                            await output.WriteLineAsync(teardownResult.Detail).ConfigureAwait(false);
                        }

                        return 1;
                    }

                    await output.WriteLineAsync(
                        $"WARNING: Forced fresh setup will continue without confirming external cleanup: {teardownResult.Message}")
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(teardownResult.Detail))
                    {
                        await output.WriteLineAsync(teardownResult.Detail).ConfigureAwait(false);
                    }
                }

                Session.IInstallationStateCleaner cleaner = lifecycle.CreateStateCleaner(runtime);
                cleaner.Clear();
                log("Fresh setup cleared package-owned local state.");
                if (!teardownResult.Succeeded)
                {
                    const string residualWarning =
                        "WARNING: Forced fresh setup did not prove a pristine machine because owned external cleanup remains unresolved. " +
                        "Review the pre-reset report for residual sandbox or gateway identifiers. " +
                        "A later setup reset cannot remove resources whose ownership record was cleared; " +
                        "remove them through the backend's administrative cleanup path before treating this machine as pristine.";
                    log(residualWarning);
                    await output.WriteLineAsync(residualWarning).ConfigureAwait(false);
                }

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
        catch (OperationCanceledException)
        {
            string retryCommand = setupOptions.Fresh
                ? "clawctl setup --fresh"
                : "clawctl setup";
            log($"Setup was cancelled before it completed. Retry `{retryCommand}`.");
            await output.WriteLineAsync(
                $"OpenClaw setup was cancelled; setup may be incomplete. Rerun `{retryCommand}` to retry.")
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
        Session.SessionStatus session = runtime.Coordinator.GetRecordedStatus();
        Gateway.GatewayStateResult gateway = runtime.GatewayState.Read();
        string directory = Path.Combine(Path.GetTempPath(), "OpenClawGatewayMSIX", "fresh-reset");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"pre-reset-{Guid.NewGuid():N}.log");
        List<string> lines =
        [
            $"timestampUtc={DateTimeOffset.UtcNow:O}",
            $"applicationId={runtime.ApplicationId}",
            "report=pre-reset diagnostic metadata; credentials and local file contents are excluded"
        ];
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

        File.WriteAllLines(path, lines);
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
