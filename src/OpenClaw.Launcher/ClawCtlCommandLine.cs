using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace OpenClaw.Launcher;

internal sealed record ClawCtlHandlers
{
    public required Func<CancellationToken, Task<int>> Setup { get; init; }
    public required Func<CancellationToken, Task<int>> Status { get; init; }
    public required Func<bool, CancellationToken, Task<int>> Teardown { get; init; }
    public required Func<CancellationToken, Task<int>> PowerShell { get; init; }
}

// The clawctl command tree. Only the package-readiness surface belongs here:
// doctor, gateway, uninstall, and every other OpenClaw command is owned by the
// bundled CLI and reached through `openclaw`, which forwards its arguments
// without parsing them.
internal static class ClawCtlCommandLine
{
    public const string SetupCommandName = "setup";
    public const string StatusCommandName = "status";

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
        "Set up and manage the packaged OpenClaw application." +
        Environment.NewLine +
        Environment.NewLine +
        "Run `clawctl setup` to prepare the bundled runtime and isolated session." +
        Environment.NewLine +
        Environment.NewLine +
        "Run `openclaw <arguments>` to invoke the OpenClaw CLI.";

    public static string SetupDescription =>
        "Extract or repair the bundled Node.js runtime in package LocalState " +
        "and confirm the packaged OpenClaw application is present.";

    // runSetup stays a delegate so the command tree owns parsing and help while
    // Program keeps the readiness operation and its test seams.
    public static RootCommand Create(ClawCtlHandlers handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        Command setup = new(SetupCommandName, SetupDescription);
        setup.SetAction((_, cancellationToken) => handlers.Setup(cancellationToken));
        Command status = new(StatusCommandName, "Show the recorded isolated session without changing it.");
        status.SetAction((_, cancellationToken) => handlers.Status(cancellationToken));
        Option<bool> force = new("--force") { Description = "Skip confirmation and remove the owned session." };
        Command teardown = new("teardown", "Stop and remove the owned isolated session.");
        teardown.Options.Add(force);
        teardown.SetAction((parsed, cancellationToken) =>
            handlers.Teardown(parsed.GetValue(force), cancellationToken));
        Command powerShell = new(
            "pwsh",
            "Open an interactive PowerShell session inside the isolated agent.");
        powerShell.SetAction((_, cancellationToken) => handlers.PowerShell(cancellationToken));

        RootCommand root = new(RootDescription)
        {
            setup,
            status,
            teardown,
            powerShell
        };

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
