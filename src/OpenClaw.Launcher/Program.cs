using System.Reflection;

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
                ? await RunControlAsync(options, args, WriteDiagnostic, WriteConsoleError)
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
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null,
        Func<
            string,
            string,
            IReadOnlyList<string>,
            CancellationToken,
            Action<string>?,
            Task<int>>? launchOpenClaw = null)
    {
        NodeRuntime nodeRuntime = await (
            resolveNode ?? NodeRuntimeResolver.ResolveAsync)(
                CancellationToken.None);
        log(
            $"Using Node.js {nodeRuntime.Version} from " +
            $"{nodeRuntime.ExecutablePath}.");
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Using the OpenClaw application directly from the package.");
        return await (launchOpenClaw ?? GatewayLauncher.RunAsync)(
            nodeRuntime.ExecutablePath,
            applicationDirectory,
            options.OpenClawArguments,
            CancellationToken.None,
            log);
    }

    internal static async Task<int> RunControlAsync(
        HostOptions options,
        IReadOnlyList<string> args,
        Action<string> log,
        Action<string> writeError,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null,
        TextWriter? output = null)
    {
        TextWriter commandOutput = output ?? Console.Out;
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
                ClawCtlConsole.WriteHelp(commandOutput);
                return 0;
            case ClawCtlCommand.Version:
                commandOutput.WriteLine(
                    Assembly.GetExecutingAssembly().GetName().Version?.ToString() ??
                    "unknown");
                return 0;
            case ClawCtlCommand.Setup:
            {
                NodeRuntime nodeRuntime = await (
                    resolveNode ?? NodeRuntimeResolver.ResolveAsync)(
                        CancellationToken.None);
                ClawCtlConsole.WriteNodeRuntimeSummary(commandOutput, nodeRuntime);
                string applicationDirectory =
                    GetPackagedApplicationDirectory(options);
                log("Confirmed the packaged OpenClaw application is present.");
                ClawCtlConsole.WriteReadinessSummary(
                    commandOutput,
                    applicationDirectory);
                return 0;
            }
            default:
                throw new InvalidOperationException("Unknown clawctl command.");
        }
    }

    private static string GetPackagedApplicationDirectory(HostOptions options)
    {
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
