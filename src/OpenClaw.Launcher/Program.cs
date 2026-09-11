using System.CommandLine;
using System.Diagnostics.CodeAnalysis;

namespace OpenClaw.Launcher;

internal static class Program
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "Main is the process last-chance handler. Every narrower catch in " +
            "this assembly uses an exception filter; this one deliberately does " +
            "not, because narrowing it would replace the diagnostic log entry, " +
            "the user-facing error message, and the deterministic exit code 1 " +
            "with an unhandled-exception crash.")]
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
                    Console.Out,
                    Console.Error).ConfigureAwait(false)
                : await RunAgentAsync(options, WriteDiagnostic)
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
            GatewayLauncher.RunAsync).ConfigureAwait(false);

    // launchOpenClaw is a test seam: tests substitute a fake in place of
    // GatewayLauncher.RunAsync so they can assert launch behavior without
    // starting a real Node child process or Windows job object.
    internal static async Task<int> RunAgentAsync(
        HostOptions options,
        Action<string> log,
        Func<CancellationToken, Task<NodeRuntime>> resolveNode,
        LaunchOpenClawAsync launchOpenClaw)
    {
        NodeRuntime nodeRuntime = await resolveNode(CancellationToken.None)
            .ConfigureAwait(false);
        log(
            $"Using Node.js {nodeRuntime.Version} from " +
            $"{nodeRuntime.ExecutablePath}.");
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Using the OpenClaw application directly from the package.");
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
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null)
    {
        RootCommand command = ClawCtlCommandLine.Create(
            cancellationToken => RunSetupAsync(
                options,
                log,
                output,
                resolveNode,
                cancellationToken));

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
        HostOptions options,
        Action<string> log,
        TextWriter output,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode,
        CancellationToken cancellationToken)
    {
        NodeRuntime nodeRuntime = await (
            resolveNode ?? NodeRuntimeResolver.ResolveAsync)(
                cancellationToken).ConfigureAwait(false);
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
}
