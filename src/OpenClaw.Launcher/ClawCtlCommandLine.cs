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
    public Func<CancellationToken, Task<int>> Open { get; init; } = _ => Task.FromResult(1);
    public Func<CompletionOptions, CancellationToken, Task<int>> Completion { get; init; } = (_, _) => Task.FromResult(1);
    public required Func<PowerShellOptions, CancellationToken, Task<int>> PowerShell { get; init; }
    public required Func<bool, CancellationToken, Task<int>> GatewayStart { get; init; }
    public required Func<CancellationToken, Task<int>> GatewayStatus { get; init; }
    public required Func<CancellationToken, Task<int>> GatewayStop { get; init; }
    public required Func<CancellationToken, Task<int>> GatewayRestart { get; init; }
}

internal sealed record SetupOptions(bool Fresh, bool Force);
internal sealed record CompletionOptions(bool Install, bool Uninstall, string? ProfilePath);
internal sealed record PowerShellOptions(
    string? Command,
    string? File,
    IReadOnlyList<string> Arguments);

internal sealed class ClawCtlOutputOptions
{
    public bool Json { get; set; }

    public bool NoColor { get; set; }
}

// The clawctl command tree. Only the package-readiness surface belongs here:
// doctor, gateway, uninstall, and every other OpenClaw command is owned by the
// bundled CLI and reached through `openclaw`, which forwards its arguments
// without parsing them.
// Keep the curated command references in plugins/gateway-isolation/index.js and
// index.test.js synchronized when exposed commands or their descriptions change.
internal static class ClawCtlCommandLine
{
    public const string SetupCommandName = "setup";
    public const string StatusCommandName = "status";
    public const string CollectLogsCommandName = "collect-logs";
    public const string OpenCommandName = "open";
    public const string CompletionCommandName = "completion";

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
        Option<bool> json = new("--json")
        {
            Description = "Write a machine-readable JSON result.",
            Recursive = true
        };
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
            outputOptions.Json = parsed.GetValue(json);
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
            outputOptions.Json = parsed.GetValue(json);
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
            outputOptions.Json = parsed.GetValue(json);
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
            outputOptions.Json = parsed.GetValue(json);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.Teardown(parsed.GetValue(teardownForce), cancellationToken);
        });
        Command open = new(
            OpenCommandName,
            "Open the running gateway's Control UI in the default browser.");
        open.SetAction((parsed, cancellationToken) =>
        {
            outputOptions.Json = parsed.GetValue(json);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.Open(cancellationToken);
        });
        Option<bool> installCompletion = new("--install")
        {
            Description = "Install completion into the PowerShell profile."
        };
        Option<bool> uninstallCompletion = new("--uninstall")
        {
            Description = "Remove completion from the PowerShell profile."
        };
        Option<string?> completionProfile = new("--profile")
        {
            Description = "PowerShell profile path to update."
        };
        Command completion = new(
            CompletionCommandName,
            "Write PowerShell completion for clawctl.");
        completion.Options.Add(installCompletion);
        completion.Options.Add(uninstallCompletion);
        completion.Options.Add(completionProfile);
        completion.Validators.Add(result =>
        {
            bool install = result.GetValue(installCompletion);
            bool uninstall = result.GetValue(uninstallCompletion);
            if (install && uninstall)
            {
                result.AddError("'--install' and '--uninstall' cannot be used together.");
            }
            if (!string.IsNullOrWhiteSpace(result.GetValue(completionProfile)) &&
                !install && !uninstall)
            {
                result.AddError("'--profile' requires '--install' or '--uninstall'.");
            }
        });
        completion.SetAction((parsed, cancellationToken) =>
        {
            outputOptions.Json = parsed.GetValue(json);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.Completion(
                new CompletionOptions(
                    parsed.GetValue(installCompletion),
                    parsed.GetValue(uninstallCompletion),
                    parsed.GetValue(completionProfile)),
                cancellationToken);
        });
        Command powerShell = new(
            "pwsh",
            "Open PowerShell or run PowerShell inside the isolated agent. " +
            "`openclaw` and `node` are available there; `clawctl` manages the " +
            "session from outside it.");
        Option<string?> powerShellCommand = new("--command")
        {
            Description = "Run one PowerShell command string and exit.",
            HelpName = "text"
        };
        Option<string?> powerShellFile = new("--file")
        {
            Description = "Run an agent-visible PowerShell script and exit.",
            HelpName = "path"
        };
        Argument<string[]> powerShellArguments = new("arguments")
        {
            Description = "Arguments passed to the script selected by --file.",
            Arity = ArgumentArity.ZeroOrMore
        };
        powerShell.Options.Add(powerShellCommand);
        powerShell.Options.Add(powerShellFile);
        powerShell.Arguments.Add(powerShellArguments);
        powerShell.Validators.Add(result =>
        {
            if (result.GetValue(json))
            {
                result.AddError(
                    "'--json' is not supported for 'pwsh', which preserves PowerShell streams.");
            }

            string? command = result.GetValue(powerShellCommand);
            string? file = result.GetValue(powerShellFile);
            string[] arguments = result.GetValue(powerShellArguments) ?? [];
            if (command is not null && file is not null)
            {
                result.AddError("'--command' and '--file' cannot be used together.");
            }
            if (command is not null && string.IsNullOrWhiteSpace(command))
            {
                result.AddError("'--command' requires non-empty PowerShell text.");
            }
            if (file is not null && string.IsNullOrWhiteSpace(file))
            {
                result.AddError("'--file' requires a non-empty script path.");
            }
            if (arguments.Length > 0 && file is null)
            {
                result.AddError("PowerShell script arguments require '--file'.");
            }
        });
        powerShell.SetAction((parsed, cancellationToken) =>
        {
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.PowerShell(
                new PowerShellOptions(
                    parsed.GetValue(powerShellCommand),
                    parsed.GetValue(powerShellFile),
                    parsed.GetValue(powerShellArguments) ?? []),
                cancellationToken);
        });
        Command gateway = new(
            "gateway-service",
            "Manage the background OpenClaw gateway inside the isolated session.");
        Command gatewayStart = new("start", "Start the gateway if needed.");
        Option<bool> recovery = new("--recovery") { Hidden = true };
        gatewayStart.Options.Add(recovery);
        gatewayStart.SetAction((parsed, token) =>
        {
            outputOptions.Json = parsed.GetValue(json);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.GatewayStart(parsed.GetValue(recovery), token);
        });
        Command gatewayStatus = new("status", "Show whether the gateway is running.");
        gatewayStatus.SetAction((parsed, token) =>
        {
            outputOptions.Json = parsed.GetValue(json);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.GatewayStatus(token);
        });
        Command gatewayStop = new("stop", "Stop the gateway but keep the session and its data.");
        gatewayStop.SetAction((parsed, token) =>
        {
            outputOptions.Json = parsed.GetValue(json);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.GatewayStop(token);
        });
        Command gatewayRestart = new("restart", "Stop the gateway and start it again.");
        gatewayRestart.SetAction((parsed, token) =>
        {
            outputOptions.Json = parsed.GetValue(json);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return handlers.GatewayRestart(token);
        });
        gateway.Subcommands.Add(gatewayStart);
        gateway.Subcommands.Add(gatewayStatus);
        gateway.Subcommands.Add(gatewayStop);
        gateway.Subcommands.Add(gatewayRestart);

        RootCommand root = new(RootDescription)
        {
            setup,
            status,
            collectLogs,
            teardown,
            open,
            completion,
            powerShell,
            gateway
        };
        root.Options.Add(json);
        root.Options.Add(noColor);

        // Bare `clawctl` is a discovery request, not a usage error, so the root
        // prints help and succeeds instead of reporting a missing command.
        var helpAction = new ClawCtlHelpAction(noColor);
        root.SetAction((parseResult, _) => Task.FromResult(helpAction.Invoke(parseResult)));
        UseLauncherVersion(root, json, noColor);
        UseClawCtlHelp(root, helpAction);

        return root;
    }

    // The built-in help action is sealed and exposes only a wrap width, so
    // replacing it is the supported way to render help. One replacement covers
    // every command: the root's help option is recursive, so a command added
    // later reaches the same action and is described from the live tree.
    private static void UseClawCtlHelp(RootCommand root, ClawCtlHelpAction helpAction)
    {
        foreach (Option option in root.Options)
        {
            if (option is HelpOption helpOption)
            {
                helpOption.Action = helpAction;
            }
        }
    }

    // The built-in version action reports the entry assembly, which is the test
    // or scenario host rather than the launcher. Report the build identity that
    // was compiled into this binary so the value identifies the shipped package
    // in every host.
    private static void UseLauncherVersion(
        RootCommand root,
        Option<bool> json,
        Option<bool> noColor)
    {
        foreach (Option option in root.Options)
        {
            if (option is VersionOption versionOption)
            {
                versionOption.Action = new LauncherVersionAction(json, noColor);
            }
        }
    }

    private sealed class LauncherVersionAction(Option<bool> json, Option<bool> noColor)
        : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            TextWriter output = parseResult.InvocationConfiguration.Output;
            bool jsonValue = GetBooleanValue(parseResult, json, defaultValue: false);
            if (jsonValue)
            {
                ClawCtlJson.WriteVersion(output);
                return 0;
            }

            IDisposable? restore = null;
            bool useColor = ClawCtlColorPolicy.PrepareOutput(
                GetBooleanValue(parseResult, noColor, defaultValue: true),
                json: false,
                ReferenceEquals(output, Console.Out),
                WindowsHostConsole.Instance.IsInteractive,
                Environment.GetEnvironmentVariable,
                () => WindowsHostConsole.Instance
                    .TryEnableVirtualTerminalProcessing(output, _ => { }, out restore));

            using (restore)
            {
                ClawCtlConsole.WriteVersion(output, useColor);
            }

            return 0;
        }

        private static bool GetBooleanValue(
            ParseResult parseResult,
            Option<bool> option,
            bool defaultValue)
        {
            try
            {
                return parseResult.GetValue(option);
            }
            catch (InvalidOperationException)
            {
                return defaultValue;
            }
        }
    }
}
