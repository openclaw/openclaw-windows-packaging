using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace OpenClaw.Launcher;

// The operations the clawctl command tree invokes. Parsing, help, and
// completion stay in the command tree; the operations themselves stay in
// Program, where their collaborators can be substituted by tests.
internal sealed record ClawCtlHandlers
{
    public required Func<CancellationToken, Task<int>> Setup { get; init; }

    public required Func<CancellationToken, Task<int>> SessionStatus { get; init; }

    public required Func<CancellationToken, Task<int>> SessionStop { get; init; }

    public required Func<CancellationToken, Task<int>> SessionRemove { get; init; }

    public required Func<CancellationToken, Task<int>> GatewayInstall { get; init; }

    public required Func<CancellationToken, Task<int>> GatewayStatus { get; init; }

    public required Func<CancellationToken, Task<int>> GatewayStart { get; init; }

    public required Func<CancellationToken, Task<int>> GatewayStop { get; init; }

    public required Func<CancellationToken, Task<int>> GatewayUninstall { get; init; }

    public required Func<CancellationToken, Task<int>> GatewayDiagnose { get; init; }
}

// The clawctl command tree. Only host-owned package, session, and managed
// gateway management belongs here: doctor, gateway, uninstall, and every other
// OpenClaw command is owned by the bundled CLI and reached through `openclaw`,
// which forwards its arguments without parsing them.
internal static class ClawCtlCommandLine
{
    public const string SetupCommandName = "setup";

    public const string SessionCommandName = "session";

    // Deliberately not `gateway`. OpenClaw itself owns `openclaw gateway run`,
    // and a host command called `gateway` would shadow it.
    public const string GatewayCommandName = "gateway-service";

    // Response-file expansion is off. A leading `@` means nothing to clawctl,
    // so it is reported as an unrecognized argument instead of silently reading
    // a file from disk. `openclaw` already forwards such a token to the
    // OpenClaw CLI untouched, and leaving the two entrypoints consistent
    // matters more than the convenience. Completion stays enabled.
    public static ParserConfiguration CreateParserConfiguration() => new()
    {
        ResponseFileTokenReplacer = null
    };

    // Node guidance is part of help rather than only a launch-time failure so
    // that a user can discover the prerequisite before running anything.
    public static string RootDescription =>
        "Set up and manage the packaged OpenClaw application." +
        Environment.NewLine +
        Environment.NewLine +
        $"Prerequisite: install Node.js {NodeRuntimeResolver.SupportedVersions}." +
        Environment.NewLine +
        $"  {NodeRuntimeResolver.InstallCommand}" +
        Environment.NewLine +
        Environment.NewLine +
        "Run `clawctl setup` once before `openclaw <arguments>`.";

    public static string SetupDescription =>
        "Provision the isolated session, save the gateway launch configuration, " +
        "and enable gateway startup at sign-in. The gateway is not started." +
        Environment.NewLine +
        $"Requires Node.js {NodeRuntimeResolver.SupportedVersions}.";

    public static RootCommand Create(ClawCtlHandlers handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        Command setup = new(SetupCommandName, SetupDescription);
        setup.SetAction((_, cancellationToken) => handlers.Setup(cancellationToken));

        RootCommand root = new(RootDescription)
        {
            setup,
            CreateSessionCommand(handlers),
            CreateGatewayCommand(handlers)
        };

        // Bare `clawctl` is a discovery request, not a usage error, so the root
        // prints help and succeeds instead of reporting a missing command.
        root.SetAction((parseResult, _) => Task.FromResult(WriteHelp(parseResult)));
        UseLauncherVersion(root);

        return root;
    }

    private static Command CreateSessionCommand(ClawCtlHandlers handlers)
    {
        // A bare noun prints its own help rather than defaulting to a
        // sub-command, so a mistyped destructive verb can never resolve to a
        // different operation than the user typed.
        Command session = new(
            SessionCommandName,
            "Inspect and manage the isolated session this installation owns.");

        Command status = new(
            "status",
            "Show the recorded isolated session. Reads only; changes nothing.");
        status.SetAction((_, token) => handlers.SessionStatus(token));

        Command stop = new(
            "stop",
            "Stop the isolated session, keeping its profile and data.");
        stop.SetAction((_, token) => handlers.SessionStop(token));

        Command remove = new(
            "remove",
            "Stop and deprovision the isolated session. This destroys its " +
            "guest profile and workspace contents.");
        remove.SetAction((_, token) => handlers.SessionRemove(token));

        session.Subcommands.Add(status);
        session.Subcommands.Add(stop);
        session.Subcommands.Add(remove);
        session.SetAction((parseResult, _) => Task.FromResult(WriteHelp(parseResult)));
        return session;
    }

    private static Command CreateGatewayCommand(ClawCtlHandlers handlers)
    {
        Command gateway = new(
            GatewayCommandName,
            "Manage the background OpenClaw gateway and its sign-in recovery.");

        Command install = new(
            "install",
            "Start the gateway and restart it when you sign in to Windows.");
        install.SetAction((_, token) => handlers.GatewayInstall(token));

        Command status = new(
            "status",
            "Show the gateway and its sign-in recovery. Reads only; changes " +
            "nothing.");
        status.SetAction((_, token) => handlers.GatewayStatus(token));

        Command start = new("start", "Start the gateway if it is not running.");
        start.SetAction((_, token) => handlers.GatewayStart(token));

        Command stop = new(
            "stop",
            "Stop the gateway, keeping the session and its data.");
        stop.SetAction((_, token) => handlers.GatewayStop(token));

        Command uninstall = new(
            "uninstall",
            "Stop the gateway and remove its sign-in recovery.");
        uninstall.SetAction((_, token) => handlers.GatewayUninstall(token));

        Command diagnose = new(
            "diagnose",
            "Explain why the gateway is or is not running. Reads only; " +
            "changes nothing.");
        diagnose.SetAction((_, token) => handlers.GatewayDiagnose(token));

        gateway.Subcommands.Add(install);
        gateway.Subcommands.Add(status);
        gateway.Subcommands.Add(start);
        gateway.Subcommands.Add(stop);
        gateway.Subcommands.Add(uninstall);
        gateway.Subcommands.Add(diagnose);
        gateway.SetAction((parseResult, _) => Task.FromResult(WriteHelp(parseResult)));
        return gateway;
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
