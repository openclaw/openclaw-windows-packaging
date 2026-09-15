using System.CommandLine;

namespace OpenClaw.Launcher.Tests;

// clawctl keeps System.CommandLine's completion support but deliberately
// disables response-file expansion. These cases exist so a later "restore the
// library defaults" change cannot silently reintroduce file reads driven by an
// argument, or drop completion.
public sealed class ClawCtlParserDefaultsTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    private static Task<NodeRuntime> FailIfSetupRuns(CancellationToken _) =>
        throw new InvalidOperationException(
            "Setup ran for an invocation that should not have started it.");

    private static async Task<(int ExitCode, string Output)> RunAsync(
        params string[] args)
    {
        using var output = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []),
            args,
            _ => { },
            output,
            TextWriter.Null,
            FailIfSetupRuns).ConfigureAwait(false);

        return (exitCode, output.ToString());
    }

    private string CreateResponseFile(string name, params string[] lines)
    {
        string path = Path.Combine(_testDirectory, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    // A readable file holding a valid command is the case that would succeed if
    // expansion were ever re-enabled, so it is the one worth asserting on.
    [Fact]
    public async Task ResponseFileTokenIsRejectedRatherThanExpanded()
    {
        string responseFile = CreateResponseFile("commands.rsp", "setup");

        (int exitCode, string output) =
            await RunAsync($"@{responseFile}").ConfigureAwait(true);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(
            "package is ready",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseFileTokenForAMissingFileIsAlsoJustAnArgument()
    {
        string missing = Path.Combine(_testDirectory, "absent.rsp");

        (int exitCode, _) = await RunAsync($"@{missing}").ConfigureAwait(true);

        Assert.Equal(1, exitCode);
    }

    // Completion is a parse-time query. It must never start the readiness
    // operation, because a shell issues it on every keystroke.
    [Fact]
    public void CompletionSuggestsCommandsWithoutResolvingNode()
    {
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = _ => throw new InvalidOperationException(
                "Completion started the readiness operation."),
            Status = _ => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0)
        });

        IEnumerable<string> completions = root
            .Parse("se", ClawCtlCommandLine.CreateParserConfiguration())
            .GetCompletions()
            .Select(completion => completion.Label);

        Assert.Contains(
            ClawCtlCommandLine.SetupCommandName,
            completions,
            StringComparer.Ordinal);
    }

    // The directive is the only completion path a shell can actually reach, so
    // it is covered through the real dispatcher rather than the in-process
    // completion API alone.
    [Fact]
    public async Task CompletionDirectiveWritesSuggestionsWithoutRunningSetup()
    {
        (int exitCode, string output) =
            await RunAsync("[suggest:2]", "se").ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            ClawCtlCommandLine.SetupCommandName,
            output,
            StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_testDirectory, recursive: true);
}
