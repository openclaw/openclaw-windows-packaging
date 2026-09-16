using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace OpenClaw.Launcher;

internal sealed record ClawCtlHandlers
{
    public required Func<SetupOptions, CancellationToken, Task<int>> Setup { get; init; }
    public required Func<CancellationToken, Task<int>> Status { get; init; }
    public required Func<string?, CancellationToken, Task<int>> CollectLogs { get; init; }
    public required Func<bool, CancellationToken, Task<int>> Teardown { get; init; }
    public required Func<CancellationToken, Task<int>> PowerShell { get; init; }
    public required Func<CancellationToken, Task<int>> GatewayStart { get; init; }
    public required Func<CancellationToken, Task<int>> GatewayStatus { get; init; }
    public required Func<CancellationToken, Task<int>> GatewayStop { get; init; }
}

internal sealed record SetupOptions(bool Fresh, bool Force);

internal sealed class ClawCtlOutputOptions
{
    public bool NoColor { get; set; }
}

// The clawctl command tree. Only the package-readiness surface belongs here:
// doctor, gateway, uninstall, and every other OpenClaw command is owned by the
// bundled CLI and reached through `openclaw`, which forwards its arguments
// without parsing them.
internal static class ClawCtlCommandLine
{
    public const string SetupCommandName = "setup";
    public const string StatusCommandName = "status";
    public const string CollectLogsCommandName = "collect-logs";

    // Response-file expansion is off. A leading `@` means nothing to clawctl,
    // so it is reported as an unrecognized argument instead of silently reading
    // a file from disk. `openclaw` already forwards such a token to the
    // OpenClaw CLI untouched, and leaving the two entrypoints consistent
    // matters more than the convenience. Completion stays enabled.
    public static ParserConfiguration CreateParserConfiguration() => new()
    {
        ResponseFileTokenReplacer = null
    };

    // Setup guidance is available without preparing the runtime.
    public static string RootDescription =>
        "Get packaged OpenClaw and its bundled Node.js runtime ready, and manage its isolated session." +
        Environment.NewLine +
        Environment.NewLine +
        "Run `clawctl setup` before using OpenClaw for the first time." +
        Environment.NewLine +
        Environment.NewLine +
        "Run `openclaw <arguments>` to invoke the OpenClaw CLI.";

    public static string SetupDescription =>
        "Provision or reuse the owned isolated session, extract or repair the " +
        "bundled Node.js runtime, and confirm the packaged OpenClaw " +
        "application is present. Requires a Windows build that supports " +
        "isolated agent sessions.";

    // runSetup stays a delegate so the command tree owns parsing and help while
    // Program keeps the readiness operation and its test seams.
    public static RootCommand Create(
        ClawCtlHandlers handlers,
        ClawCtlOutputOptions? outputOptions = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        outputOptions ??= new ClawCtlOutputOptions();
        Option<bool> noColor = new("--no-color")
        {
            Description = "Disable colored output.",
            Recursive = true
        };
        Command setup = new(SetupCommandName, SetupDescription);
        Option<bool> fresh = new("--fresh")
        {
            Description = "Remove this installation's owned session and local state before setting it up again."
        };
        Option<bool> force = new("--force")
        {
            Description = "Continue with package-local cleanup when owned external cleanup cannot be confirmed. Requires --fresh."
        };
        setup.Options.Add(fresh);
        setup.Options.Add(force);
        setup.Validators.Add(result =>
        {
            if (result.GetValue(force) && !result.GetValue(fresh))
            {
                result.AddError("Option '--force' requires option '--fresh'.");
            }
        });
        setup.SetAction((parsed, cancellationToken) =>
        {
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.Setup(
                new SetupOptions(
                    parsed.GetValue(fresh),
                    parsed.GetValue(force)),
                cancellationToken);
        });
        Command status = new(
            StatusCommandName,
            "Show whether the session, gateway, and sign-in recovery are ready.");
        status.SetAction((parsed, cancellationToken) =>
        {
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.Status(cancellationToken);
        });
        Option<string?> outputPath = new("--output")
        {
            Description = "Path for the diagnostics ZIP file."
        };
        Command collectLogs = new(
            CollectLogsCommandName,
            "Gather redacted diagnostics into one ZIP file for troubleshooting.");
        collectLogs.Options.Add(outputPath);
        collectLogs.SetAction((parsed, cancellationToken) =>
        {
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.CollectLogs(parsed.GetValue(outputPath), cancellationToken);
        });
        Option<bool> teardownForce = new("--force")
        {
            Description = "Remove the session without asking for confirmation."
        };
        Command teardown = new("teardown", "Remove OpenClaw's isolated session and gateway.");
        teardown.Options.Add(teardownForce);
        teardown.SetAction((parsed, cancellationToken) =>
        {
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.Teardown(parsed.GetValue(teardownForce), cancellationToken);
        });
        Command powerShell = new(
            "pwsh",
            "Open PowerShell inside the isolated agent. `openclaw` and `node` " +
            "are available there; `clawctl` manages the session from outside it.");
        powerShell.SetAction((parsed, cancellationToken) =>
        {
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.PowerShell(cancellationToken);
        });
        Command gateway = new(
            "gateway-service",
            "Manage the background OpenClaw gateway inside the isolated session.");
        Command gatewayStart = new("start", "Start the gateway if needed.");
        gatewayStart.SetAction((parsed, token) =>
        {
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.GatewayStart(token);
        });
        Command gatewayStatus = new("status", "Show whether the gateway is running.");
        gatewayStatus.SetAction((parsed, token) =>
        {
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.GatewayStatus(token);
        });
        Command gatewayStop = new("stop", "Stop the gateway but keep the session and its data.");
        gatewayStop.SetAction((parsed, token) =>
        {
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.GatewayStop(token);
        });
        gateway.Subcommands.Add(gatewayStart);
        gateway.Subcommands.Add(gatewayStatus);
        gateway.Subcommands.Add(gatewayStop);

        RootCommand root = new(RootDescription)
        {
            setup,
            status,
            collectLogs,
            teardown,
            powerShell,
            gateway
        };
        root.Options.Add(noColor);

        // Bare `clawctl` is a discovery request, not a usage error, so the root
        // prints help and succeeds instead of reporting a missing command.
        root.SetAction((parseResult, _) => Task.FromResult(WriteHelp(parseResult)));
        UseLauncherVersion(root);

        return root;
    }

    private static int WriteHelp(ParseResult parseResult)
    {
        HelpAction help = new();
        return help.Invoke(parseResult);
    }

    // The built-in version action reports the entry assembly, which is the test
    // or scenario host rather than the launcher. Report the launcher assembly so
    // the value identifies the shipped package binary in every host.
    private static void UseLauncherVersion(RootCommand root)
    {
        foreach (Option option in root.Options)
        {
            if (option is VersionOption versionOption)
            {
                versionOption.Action = new LauncherVersionAction();
            }
        }
    }

    private sealed class LauncherVersionAction : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            string version = typeof(LauncherVersionAction).Assembly
                .GetName()
                .Version?
                .ToString() ?? "unknown";
            parseResult.InvocationConfiguration.Output.WriteLine(version);
            return 0;
        }
    }
}
