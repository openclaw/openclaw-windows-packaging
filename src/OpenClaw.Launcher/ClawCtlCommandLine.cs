using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace OpenClaw.Launcher;

// The clawctl command tree. Only the package-readiness surface belongs here:
// doctor, gateway, uninstall, and every other OpenClaw command is owned by the
// bundled CLI and reached through `openclaw`, which forwards its arguments
// without parsing them.
internal static class ClawCtlCommandLine
{
    public const string SetupCommandName = "setup";

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
        "Verify that this device can run the packaged OpenClaw application." +
        Environment.NewLine +
        Environment.NewLine +
        $"Prerequisite: install Node.js {NodeRuntimeResolver.SupportedVersions}." +
        Environment.NewLine +
        $"  {NodeRuntimeResolver.InstallCommand}" +
        Environment.NewLine +
        Environment.NewLine +
        "Run `openclaw <arguments>` to invoke the OpenClaw CLI.";

    public static string SetupDescription =>
        "Check for a compatible Node.js runtime and confirm the packaged " +
        "OpenClaw application is present. Reads only; changes nothing." +
        Environment.NewLine +
        $"Requires Node.js {NodeRuntimeResolver.SupportedVersions}.";

    // runSetup stays a delegate so the command tree owns parsing and help while
    // Program keeps the readiness operation and its test seams.
    public static RootCommand Create(Func<CancellationToken, Task<int>> runSetup)
    {
        Command setup = new(SetupCommandName, SetupDescription);
        setup.SetAction((_, cancellationToken) => runSetup(cancellationToken));

        RootCommand root = new(RootDescription)
        {
            setup
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
