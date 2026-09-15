using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using LauncherProgram = OpenClaw.Launcher.Program;

namespace OpenClaw.Launcher.AotSmoke;

// A NativeAOT scenario driver for the launcher's startup path.
//
// Two things cannot be proven by the xUnit suite, because it runs under a JIT
// test host: the alias and root-command names that come from native argv[0],
// and that command-line parsing, help rendering, and completion survive
// trimming and ahead-of-time compilation.
//
// This driver runs the same Program.RunAsync that Main runs, so startup
// diagnostics, argument routing, the operational error boundary, and disposal
// are all exercised. Every collaborator it injects is fixture-owned: the
// diagnostic log is created at an explicit path the driver owns, the writers are
// in-memory, and the Node and launch delegates cannot start a real process.
// Nothing here reads or writes the user's profile.
internal static class SmokeProgram
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "This is the scenario harness boundary. A scenario that fails with " +
            "any exception type must be reported as a failed check with a " +
            "nonzero exit code, not crash the driver and hide the remaining " +
            "scenarios.")]
    private static async Task<int> Main()
    {
        (string Name, Func<Task> RunAsync)[] scenarios =
        [
            ("native alias resolves to clawctl", NativeAliasResolvesToControlAsync),
            ("bare clawctl prints help", BareInvocationPrintsHelpAsync),
            ("--help prints help", HelpOptionPrintsHelpAsync),
            ("setup --help prints command help", SetupHelpPrintsCommandHelpAsync),
            ("--version reports the launcher", VersionReportsLauncherAssemblyAsync),
            ("--version wins over trailing arguments", VersionWinsOverTrailingAsync),
            ("unknown command fails", UnknownCommandFailsAsync),
            ("response-file token is not expanded", ResponseFileTokenIsNotExpandedAsync),
            ("completion directive suggests commands", CompletionDirectiveSuggestsAsync),
            ("unpackaged setup reports identity failure", SetupReportsReadinessAsync),
            ("missing application reports diagnostics", MissingApplicationReportsAsync),
            ("openclaw forwards arguments verbatim", AgentForwardsArgumentsAsync)
        ];

        int failures = 0;

        // Console writes go through non-async helpers so this harness reports
        // results the same way in the middle of an async scenario loop.
        static void WriteLine(string message) => Console.Out.WriteLine(message);
        static void WriteError(string message) => Console.Error.WriteLine(message);

        foreach ((string name, Func<Task> runAsync) in scenarios)
        {
            try
            {
                await runAsync().ConfigureAwait(false);
                WriteLine($"  ok    {name}");
            }
            catch (Exception exception)
            {
                failures++;
                WriteError($"  FAIL  {name}");
                WriteError($"        {exception.Message}");
            }
        }

        if (failures != 0)
        {
            WriteError($"{failures} NativeAOT scenario(s) failed.");
            return 1;
        }

        WriteLine($"{scenarios.Length} NativeAOT scenarios passed.");
        return 0;
    }

    // The management entrypoint is selected from the native command line. This
    // is the check the JIT suite cannot make, because there the first argument
    // is the test host rather than clawctl.
    private static Task NativeAliasResolvesToControlAsync()
    {
        HostEntrypoint resolved = HostEntrypointResolver.Resolve();
        Assert(
            resolved == HostEntrypoint.Control,
            $"Expected the native alias to resolve to Control but got {resolved}. " +
            "This driver must run as clawctl.exe.");
        return Task.CompletedTask;
    }

    private static async Task BareInvocationPrintsHelpAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync([]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);

        // The root command name comes from native argv[0]; a JIT host would
        // render the test runner's name here instead.
        AssertContains(fixture.Output.ToString(), "clawctl", fixture);
        AssertContains(fixture.Output.ToString(), "setup", fixture);
        AssertContains(fixture.Output.ToString(), "Node.js", fixture);
        fixture.AssertNodeWasNotResolved();
        fixture.AssertLogRecordsStartupAndExit();
    }

    private static async Task HelpOptionPrintsHelpAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["--help"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        AssertContains(fixture.Output.ToString(), "setup", fixture);
        AssertContains(fixture.Output.ToString(), "--version", fixture);
        fixture.AssertNodeWasNotResolved();
    }

    private static async Task SetupHelpPrintsCommandHelpAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["setup", "--help"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        AssertContains(fixture.Output.ToString(), "clawctl setup", fixture);
        fixture.AssertNodeWasNotResolved();
    }

    // This driver's assembly version is 9.9.9.9. The library's built-in action
    // reports the entry assembly, so if the custom action were ever dropped
    // this scenario would print 9.9.9.9 instead of the launcher's version.
    private static async Task VersionReportsLauncherAssemblyAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();
        string launcherVersion = LauncherVersion();
        string driverVersion = DriverVersion();

        int exitCode = await fixture.RunAsync(["--version"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        string reported = fixture.Output.ToString().Trim();
        Assert(
            string.Equals(reported, launcherVersion, StringComparison.Ordinal),
            $"Expected the launcher version '{launcherVersion}' but got '{reported}'.");
        Assert(
            !string.Equals(reported, driverVersion, StringComparison.Ordinal),
            $"Reported this driver's version '{driverVersion}' instead of the launcher's.");
    }

    private static async Task VersionWinsOverTrailingAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["--version", "bogus"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        Assert(
            string.Equals(
                fixture.Output.ToString().Trim(),
                LauncherVersion(),
                StringComparison.Ordinal),
            "Expected the launcher version with a trailing argument present.");
    }

    private static async Task UnknownCommandFailsAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["bogus"]).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        AssertContains(fixture.Error.ToString(), "bogus", fixture);
        fixture.AssertNodeWasNotResolved();
    }

    // Response-file expansion is disabled, so a readable file behind an `@`
    // token must still be rejected as an unrecognized argument.
    private static async Task ResponseFileTokenIsNotExpandedAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();
        string responseFile = Path.Combine(fixture.Root, "help.rsp");
        await File.WriteAllTextAsync(responseFile, "--help").ConfigureAwait(false);

        int exitCode = await fixture.RunAsync([$"@{responseFile}"]).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        fixture.AssertNodeWasNotResolved();
    }

    private static async Task CompletionDirectiveSuggestsAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["[suggest:2]", "se"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        AssertContains(fixture.Output.ToString(), "setup", fixture);
        fixture.AssertNodeWasNotResolved();
    }

    private static async Task SetupReportsReadinessAsync()
    {
        using Fixture fixture = await Fixture.CreateWithApplicationAsync().ConfigureAwait(false);

        int exitCode = await fixture.RunAsync(["setup"]).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        AssertContains(fixture.Output.ToString(), fixture.ApplicationDirectory, fixture);
        AssertContains(
            fixture.Output.ToString(),
            "not running from its installed package",
            fixture);
        fixture.AssertLogRecordsStartupAndExit();
        Assert(
            File.Exists(fixture.EntryPoint),
            "The readiness check removed or replaced the fixture entry point.");
    }

    // The operational error boundary is part of startup, not of the parser.
    // It must still name the diagnostic log, which here is the fixture's.
    private static async Task MissingApplicationReportsAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["setup"]).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        AssertContains(fixture.Error.ToString(), fixture.LogPath, fixture);
        Assert(
            !fixture.Output.ToString().Contains("package is ready", StringComparison.Ordinal),
            "A failed readiness check still reported success.");
        fixture.AssertLogRecordsStartupAndExit();
    }

    // The agent entrypoint must never consult System.CommandLine. These tokens
    // are all meaningful to the clawctl parser and must survive untouched.
    private static async Task AgentForwardsArgumentsAsync()
    {
        string[] arguments =
            ["--help", "--version", "--", "@response.rsp", "[suggest:1]", "", "a b"];
        using Fixture fixture = await Fixture
            .CreateWithApplicationAsync(HostEntrypoint.Agent)
            .ConfigureAwait(false);

        int exitCode = await fixture.RunAsync(arguments).ConfigureAwait(false);

        AssertExitCode(23, exitCode, fixture);
        string[]? forwarded = fixture.ForwardedArguments;
        Assert(forwarded is not null, "The agent path never reached the launch delegate.");
        Assert(
            forwarded!.Length == arguments.Length,
            $"Expected {arguments.Length} forwarded arguments but got {forwarded.Length}.");
        for (int index = 0; index < arguments.Length; index++)
        {
            Assert(
                string.Equals(forwarded[index], arguments[index], StringComparison.Ordinal),
                $"Argument {index} was rewritten from '{arguments[index]}' to " +
                $"'{forwarded[index]}'.");
        }
    }

    private static string LauncherVersion() =>
        typeof(HostStartup).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string DriverVersion() =>
        typeof(SmokeProgram).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static void AssertExitCode(int expected, int actual, Fixture fixture)
    {
        Assert(
            expected == actual,
            $"Expected exit code {expected} but got {actual}." +
            $"{Environment.NewLine}stdout: {fixture.Output}" +
            $"{Environment.NewLine}stderr: {fixture.Error}");
    }

    private static void AssertContains(string haystack, string needle, Fixture fixture)
    {
        Assert(
            haystack.Contains(needle, StringComparison.Ordinal),
            $"Expected to find '{needle}'." +
            $"{Environment.NewLine}stdout: {fixture.Output}" +
            $"{Environment.NewLine}stderr: {fixture.Error}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const string NodePath = @"C:\fixture\node.exe";

        private readonly HostEntrypoint _entrypoint;
        private bool _nodeResolved;

        private Fixture(string root, HostEntrypoint entrypoint)
        {
            Root = root;
            _entrypoint = entrypoint;
            LogPath = Path.Combine(root, "diagnostics", "openclaw.log");
            ApplicationDirectory = Path.Combine(root, "app");
            EntryPoint = Path.Combine(ApplicationDirectory, "openclaw.mjs");
        }

        public string Root { get; }

        public string LogPath { get; }

        public string ApplicationDirectory { get; }

        public string EntryPoint { get; }

        public StringWriter Output { get; } = new();

        public StringWriter Error { get; } = new();

        public string[]? ForwardedArguments { get; private set; }

        public static Fixture CreateWithoutApplication(
            HostEntrypoint entrypoint = HostEntrypoint.Control) =>
            new(CreateRoot(), entrypoint);

        public static async Task<Fixture> CreateWithApplicationAsync(
            HostEntrypoint entrypoint = HostEntrypoint.Control)
        {
            Fixture fixture = new(CreateRoot(), entrypoint);
            Directory.CreateDirectory(fixture.ApplicationDirectory);
            await File.WriteAllTextAsync(fixture.EntryPoint, "console.log('fixture');")
                .ConfigureAwait(false);
            return fixture;
        }

        public async Task<int> RunAsync(string[] args)
        {
            HostStartup startup = new()
            {
                Entrypoint = _entrypoint,
                CreateDiagnostics = () => HostDiagnosticLog.Create(LogPath),
                BaseDirectory = Root,
                Output = Output,
                Error = Error,
                ResolveNode = _ =>
                {
                    _nodeResolved = true;
                    return Task.FromResult(
                        new NodeRuntime(
                            NodePath,
                            new Version(24, 15, 0),
                            RuntimeInformation.ProcessArchitecture));
                },
                LaunchOpenClaw = (_, _, forwarded, _, _, _) =>
                {
                    ForwardedArguments = [.. forwarded];
                    return Task.FromResult(23);
                }
            };

            return await LauncherProgram.RunAsync(args, startup).ConfigureAwait(false);
        }

        public void AssertNodeWasNotResolved() =>
            Assert(
                !_nodeResolved,
                "Node resolution ran for an invocation that must not need a runtime.");

        public void AssertLogRecordsStartupAndExit()
        {
            Assert(File.Exists(LogPath), $"No diagnostic log was written at '{LogPath}'.");
            string log = File.ReadAllText(LogPath);
            Assert(
                log.Contains("Host started", StringComparison.Ordinal),
                "The diagnostic log has no startup record.");
            Assert(
                log.Contains("Host exiting", StringComparison.Ordinal),
                "The diagnostic log has no exit record.");
        }

        public void Dispose()
        {
            Output.Dispose();
            Error.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string CreateRoot()
        {
            // Scenario state lives under the driver's own output directory
            // rather than %TEMP%. The gate script publishes into a directory it
            // owns and deletes, so this stays isolated without reading an
            // environment variable a caller could point somewhere else.
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "scenarios",
                $"clawctl-aot-scenario-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
