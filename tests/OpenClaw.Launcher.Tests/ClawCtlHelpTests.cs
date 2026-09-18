using System.CommandLine;
using System.CommandLine.Help;
using System.Text.RegularExpressions;

namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlHelpTests
{
    private static ClawCtlHandlers NoopHandlers() => new()
    {
        Setup = (_, _) => Task.FromResult(0),
        Status = _ => Task.FromResult(0),
        CollectLogs = (_, _) => Task.FromResult(0),
        Teardown = (_, _) => Task.FromResult(0),
        PowerShell = _ => Task.FromResult(0),
        GatewayStart = (_, _) => Task.FromResult(0),
        GatewayStatus = _ => Task.FromResult(0),
        GatewayStop = _ => Task.FromResult(0)
    };

    private static ClawCtlHelpModel Describe(string commandPath)
    {
        RootCommand root = ClawCtlCommandLine.Create(NoopHandlers());
        ParseResult parsed = root.Parse(commandPath);
        return ClawCtlHelp.Describe(parsed.CommandResult.Command);
    }

    // The point of describing help from the live command tree: a command added
    // to the parser is documented without anyone editing the help renderer.
    [Fact]
    public void ACommandAddedToTheTreeIsDescribedWithoutTouchingHelp()
    {
        RootCommand root = ClawCtlCommandLine.Create(NoopHandlers());
        root.Subcommands.Add(new Command("foo", "Do the foo thing."));

        ClawCtlHelpModel model = ClawCtlHelp.Describe(root);

        ClawCtlHelpEntry entry = Assert.Single(model.Commands, c => c.Term == "foo");
        Assert.Equal("Do the foo thing.", entry.Description);
    }

    [Fact]
    public void HiddenCommandsAreNotDescribed()
    {
        RootCommand root = ClawCtlCommandLine.Create(NoopHandlers());
        root.Subcommands.Add(new Command("secret", "Internal.") { Hidden = true });

        ClawCtlHelpModel model = ClawCtlHelp.Describe(root);

        Assert.DoesNotContain(model.Commands, c => c.Term == "secret");
    }

    [Fact]
    public void GatewayRecoveryMarkerIsHiddenFromHelp()
    {
        ClawCtlHelpModel model = Describe("gateway-service start");

        Assert.DoesNotContain(
            model.Options,
            option => option.Term.Contains("--recovery", StringComparison.Ordinal));
    }

    // RootCommand names itself after the entry assembly, so an unguarded
    // renderer reports the test host here and the scenario driver in the
    // NativeAOT suite. Usage must always name the shipped command.
    [Fact]
    public void RootUsageNamesClawCtlRatherThanTheEntryAssembly()
    {
        ClawCtlHelpModel model = Describe(string.Empty);

        Assert.Equal(string.Empty, model.CommandPath);
        Assert.StartsWith("clawctl ", model.Usage, StringComparison.Ordinal);
        Assert.DoesNotContain("testhost", model.Usage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubcommandUsageIncludesTheCommandPath()
    {
        ClawCtlHelpModel model = Describe("setup");

        Assert.Equal("setup", model.CommandPath);
        Assert.Equal("clawctl setup [options]", model.Usage);
    }

    [Fact]
    public void NestedSubcommandUsageIncludesEveryLevel()
    {
        ClawCtlHelpModel model = Describe("gateway-service start");

        Assert.Equal("gateway-service start", model.CommandPath);
        Assert.Equal("clawctl gateway-service start [options]", model.Usage);
    }

    // --json and --no-color are declared once on the root as recursive options.
    // They are part of the command line a user can type for a subcommand, so
    // subcommand help has to report them.
    [Fact]
    public void SubcommandHelpReportsInheritedRecursiveOptions()
    {
        ClawCtlHelpModel model = Describe("setup");
        string[] terms = [.. model.Options.Select(o => o.Term)];

        Assert.Contains(terms, t => t.StartsWith("--fresh", StringComparison.Ordinal));
        Assert.Contains(terms, t => t.StartsWith("--json", StringComparison.Ordinal));
        Assert.Contains(terms, t => t.StartsWith("--no-color", StringComparison.Ordinal));
    }

    // --version is deliberately not recursive; reporting it on a subcommand
    // would describe a command line that does not parse.
    [Fact]
    public void SubcommandHelpOmitsNonRecursiveRootOptions()
    {
        ClawCtlHelpModel model = Describe("setup");

        Assert.DoesNotContain(
            model.Options,
            o => o.Term.StartsWith("--version", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsThatTakeAValueDescribeTheValue()
    {
        ClawCtlHelpModel model = Describe("collect-logs");

        ClawCtlHelpEntry output = Assert.Single(
            model.Options,
            o => o.Term.StartsWith("--output", StringComparison.Ordinal));
        Assert.Contains("<", output.Term, StringComparison.Ordinal);
    }

    // Windows-style aliases still parse; listing them widens the term column
    // on every row for a spelling nobody reads help to discover.
    [Fact]
    public void HelpTermsOmitWindowsStyleAliases()
    {
        ClawCtlHelpModel model = Describe(string.Empty);

        ClawCtlHelpEntry help = Assert.Single(
            model.Options,
            o => o.Term.StartsWith("--help", StringComparison.Ordinal));
        Assert.DoesNotContain("/", help.Term, StringComparison.Ordinal);
        Assert.Contains("-h", help.Term, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorChangesOnlyTerminalFormatting()
    {
        ClawCtlHelpModel model = Describe(string.Empty);
        using var plain = new StringWriter();
        using var colored = new StringWriter();

        ClawCtlConsole.WriteHelp(plain, model);
        ClawCtlConsole.WriteHelp(colored, model, useColor: true);

        Assert.Contains("\u001b[", colored.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            plain.ToString(),
            Regex.Replace(colored.ToString(), "\u001b\\[[0-9;]*m", string.Empty));
    }

    // The replacement has to reach every command, including ones added after
    // the tree was built, or help silently reverts to the library renderer.
    [Fact]
    public void TheClawCtlHelpActionIsUsedByEveryCommand()
    {
        RootCommand root = ClawCtlCommandLine.Create(NoopHandlers());
        root.Subcommands.Add(new Command("foo", "Do the foo thing."));

        Assert.IsType<ClawCtlHelpAction>(root.Parse("--help").Action);
        Assert.IsType<ClawCtlHelpAction>(root.Parse("setup --help").Action);
        Assert.IsType<ClawCtlHelpAction>(root.Parse("gateway-service start --help").Action);
        Assert.IsType<ClawCtlHelpAction>(root.Parse("foo --help").Action);
        Assert.DoesNotContain(
            root.Options.OfType<HelpOption>(),
            option => option.Action is HelpAction);
    }
}
