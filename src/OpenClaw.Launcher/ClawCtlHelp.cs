using System.CommandLine;
using System.CommandLine.Invocation;

namespace OpenClaw.Launcher;

internal sealed record ClawCtlHelpEntry(string Term, string? Description);

internal sealed record ClawCtlHelpModel(
    string CommandPath,
    string? Description,
    string Usage,
    IReadOnlyList<ClawCtlHelpEntry> Commands,
    IReadOnlyList<ClawCtlHelpEntry> Options);

// Help is described from the live command tree rather than a hand-maintained
// list, so a command added to ClawCtlCommandLine appears in help without an
// edit here. The only thing a new command must supply is a description.
internal static class ClawCtlHelp
{
    internal static ClawCtlHelpModel Describe(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        string relativePath = BuildRelativePath(command);
        List<ClawCtlHelpEntry> commands = [];
        foreach (Command subcommand in command.Subcommands)
        {
            if (!subcommand.Hidden)
            {
                commands.Add(new ClawCtlHelpEntry(subcommand.Name, subcommand.Description));
            }
        }

        List<ClawCtlHelpEntry> options = CollectOptions(command);
        string invocation = relativePath.Length == 0
            ? HostEntrypointResolver.ControlCommandName
            : $"{HostEntrypointResolver.ControlCommandName} {relativePath}";
        string usage = commands.Count > 0
            ? $"{invocation} <command> [options]"
            : $"{invocation} [options]";

        return new ClawCtlHelpModel(
            relativePath,
            command.Description,
            usage,
            commands,
            options);
    }

    // A command's own options, then the recursive options it inherits.
    // Omitting inherited options would under-report the command line the user
    // can actually type.
    private static List<ClawCtlHelpEntry> CollectOptions(Command command)
    {
        List<ClawCtlHelpEntry> entries = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (Option option in command.Options)
        {
            AddOption(entries, seen, option);
        }

        foreach (Command ancestor in Ancestors(command))
        {
            foreach (Option option in ancestor.Options)
            {
                if (option.Recursive)
                {
                    AddOption(entries, seen, option);
                }
            }
        }

        return entries;
    }

    private static void AddOption(
        List<ClawCtlHelpEntry> entries,
        HashSet<string> seen,
        Option option)
    {
        if (option.Hidden || !seen.Add(option.Name))
        {
            return;
        }

        entries.Add(new ClawCtlHelpEntry(DescribeTerm(option), option.Description));
    }

    private static string DescribeTerm(Option option)
    {
        // Options carry Windows-style "/h" aliases as well as POSIX ones.
        // Listing both widens the term column for every row to describe a
        // spelling nobody reads help to discover, so only POSIX forms appear.
        List<string> forms = [option.Name];
        foreach (string alias in option.Aliases)
        {
            if (alias.StartsWith('-'))
            {
                forms.Add(alias);
            }
        }

        string term = string.Join(", ", forms);
        bool takesValue = option.Arity.MaximumNumberOfValues > 0 &&
            option.ValueType != typeof(bool);
        return takesValue
            ? $"{term} <{option.HelpName ?? option.Name.TrimStart('-')}>"
            : term;
    }

    private static IEnumerable<Command> Ancestors(Command command)
    {
        foreach (Symbol parent in command.Parents)
        {
            if (parent is Command ancestor)
            {
                yield return ancestor;
                foreach (Command further in Ancestors(ancestor))
                {
                    yield return further;
                }
            }
        }
    }

    // RootCommand names itself after the entry assembly, which is the test host
    // or the scenario driver rather than the launcher, so the root contributes
    // no name here and the caller supplies the real one.
    private static string BuildRelativePath(Command command)
    {
        List<string> names = [];
        Command? current = command;
        while (current is not null)
        {
            names.Insert(0, current.Name);
            current = FirstParentCommand(current);
        }

        return names.Count <= 1 ? string.Empty : string.Join(' ', names.Skip(1));
    }

    private static Command? FirstParentCommand(Command command)
    {
        foreach (Symbol parent in command.Parents)
        {
            if (parent is Command parentCommand)
            {
                return parentCommand;
            }
        }

        return null;
    }
}

// Replaces the built-in help action so help is rendered by the same console
// the rest of clawctl uses. The built-in action is sealed and exposes only a
// wrap width, so replacement is the supported extension point; the version
// option is customised the same way and for a related reason.
internal sealed class ClawCtlHelpAction(Option<bool> noColor) : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        ClawCtlHelpModel model = ClawCtlHelp.Describe(parseResult.CommandResult.Command);
        TextWriter output = parseResult.InvocationConfiguration.Output;
        bool outputIsProcessConsoleWriter = ReferenceEquals(output, Console.Out);
        IDisposable? restore = null;
        bool noColorValue;
        try
        {
            noColorValue = parseResult.GetValue(noColor);
        }
        catch (InvalidOperationException)
        {
            noColorValue = true;
        }

        bool useColor = ClawCtlColorPolicy.PrepareOutput(
            noColorValue,
            json: false,
            outputIsProcessConsoleWriter,
            WindowsHostConsole.Instance.IsInteractive,
            Environment.GetEnvironmentVariable,
            () => WindowsHostConsole.Instance
                .TryEnableVirtualTerminalProcessing(output, _ => { }, out restore));

        using (restore)
        {
            ClawCtlConsole.WriteHelp(output, model, useColor);
        }

        return 0;
    }
}
