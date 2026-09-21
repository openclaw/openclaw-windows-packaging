using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
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
            ("gateway-service help includes restart", GatewayServiceHelpIncludesRestartAsync),
            ("--version reports the launcher", VersionReportsLauncherAssemblyAsync),
            ("--version wins over trailing arguments", VersionWinsOverTrailingAsync),
            ("unknown command fails", UnknownCommandFailsAsync),
            ("setup --force requires --fresh", SetupForceRequiresFreshAsync),
            ("response-file token is not expanded", ResponseFileTokenIsNotExpandedAsync),
            ("completion directive suggests commands", CompletionDirectiveSuggestsAsync),
            ("unpackaged setup reports identity failure", SetupReportsReadinessAsync),
            ("JSON failures survive NativeAOT", JsonFailureIsStructuredAsync),
            ("version JSON survives NativeAOT", VersionJsonIsStructuredAsync),
            ("Spectre renders clawctl output under NativeAOT", SpectreOutputRenders),
            ("gateway narration survives NativeAOT", GatewayNarrationRenders),
            ("Windows logon identity survives NativeAOT", WindowsLogonIdentityWorks),
            ("missing application reports diagnostics", MissingApplicationReportsAsync),
            ("openclaw never parses its arguments", AgentNeverParsesItsArgumentsAsync)
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

    private static Task WindowsLogonIdentityWorks()
    {
        string id = WindowsLogonSession.GetCurrentId();
        Assert(
            id.Length == 17 && id[8] == ':' &&
            id.Where(character => character != ':').All(Uri.IsHexDigit),
            $"Unexpected Windows logon identity '{id}'.");
        return Task.CompletedTask;
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
        fixture.AssertNoInstallationWorkStarted();
        fixture.AssertLogRecordsStartupAndExit();
    }

    private static async Task HelpOptionPrintsHelpAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["--help"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        AssertContains(fixture.Output.ToString(), "setup", fixture);
        AssertContains(fixture.Output.ToString(), "--version", fixture);
        fixture.AssertNoInstallationWorkStarted();
    }

    private static async Task SetupHelpPrintsCommandHelpAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["setup", "--help"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        AssertContains(fixture.Output.ToString(), "clawctl setup", fixture);
        fixture.AssertNoInstallationWorkStarted();
    }

    private static async Task GatewayServiceHelpIncludesRestartAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture
            .RunAsync(["gateway-service", "--help"])
            .ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        AssertContains(fixture.Output.ToString(), "restart", fixture);
        fixture.AssertNoInstallationWorkStarted();
    }

    // This driver's assembly version is 9.9.9.9. The library's built-in action
    // reports the entry assembly, so if the custom action were ever dropped
    // this scenario would print 9.9.9.9 instead of the baked build identity.
    private static async Task VersionReportsLauncherAssemblyAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();
        string driverVersion = DriverVersion();

        int exitCode = await fixture.RunAsync(["--version"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        string reported = fixture.Output.ToString();
        foreach (string expected in new[]
        {
            ClawCtlBuildMetadata.PackageVersion,
            ClawCtlBuildMetadata.PackageCommit,
            ClawCtlBuildMetadata.PayloadVersion,
            ClawCtlBuildMetadata.PayloadCommit
        })
        {
            Assert(
                reported.Contains(expected, StringComparison.Ordinal),
                $"Expected the version report to contain '{expected}'.");
        }

        Assert(
            !string.Equals(reported.Trim(), driverVersion, StringComparison.Ordinal),
            $"Reported this driver's version '{driverVersion}' instead of the launcher's.");
    }

    // The build identity is the one document produced outside the command
    // result path, and it adds a type to the serializer context. Source
    // generation has to cover it ahead of time or this returns an empty object.
    private static async Task VersionJsonIsStructuredAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["--version", "--json"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        using JsonDocument document = JsonDocument.Parse(fixture.Output.ToString());
        JsonElement root = document.RootElement;
        Assert(root.GetProperty("ok").GetBoolean(), "The version document reported failure.");
        Assert(
            root.GetProperty("command").GetString() == "version",
            "The version document did not name the version command.");
        Assert(
            root.GetProperty("package").GetProperty("version").GetString() ==
                ClawCtlBuildMetadata.PackageVersion,
            "The version document did not carry the baked package version.");
        Assert(
            root.GetProperty("payload").GetProperty("commit").GetString() ==
                ClawCtlBuildMetadata.PayloadCommit,
            "The version document did not carry the baked payload commit.");
    }

    private static async Task VersionWinsOverTrailingAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["--version", "bogus"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        Assert(
            fixture.Output.ToString().Contains(
                ClawCtlBuildMetadata.PackageVersion,
                StringComparison.Ordinal),
            "Expected the launcher version with a trailing argument present.");
    }

    private static async Task UnknownCommandFailsAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["bogus"]).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        AssertContains(fixture.Error.ToString(), "bogus", fixture);
        fixture.AssertNoInstallationWorkStarted();
    }

    private static async Task SetupForceRequiresFreshAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["setup", "--force"]).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        AssertContains(fixture.Error.ToString(), "requires option '--fresh'", fixture);
        fixture.AssertNoInstallationWorkStarted();
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
        fixture.AssertNoInstallationWorkStarted();
    }

    private static async Task CompletionDirectiveSuggestsAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["[suggest:2]", "se"]).ConfigureAwait(false);

        AssertExitCode(0, exitCode, fixture);
        AssertContains(fixture.Output.ToString(), "setup", fixture);
        fixture.AssertNoInstallationWorkStarted();
    }

    private static async Task SetupReportsReadinessAsync()
    {
        using Fixture fixture = await Fixture
            .CreateWithApplicationAsync(allowInstallationWork: true)
            .ConfigureAwait(false);

        int exitCode = await fixture.RunAsync(["setup"]).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        string error = FlattenRendered(fixture.Error.ToString());
        AssertContains(
            error,
            "not running from its installed package",
            fixture);
        AssertContains(error, "newer version of Windows", fixture);
        AssertContains(error, "clawctl collect-logs", fixture);

        // The attempted requirement check is visible, but nothing is reported
        // as ready when the support check refuses.
        Assert(
            fixture.Output.ToString().Contains(
                "Checking isolated-session support.",
                StringComparison.Ordinal) &&
            !fixture.Output.ToString().Contains("ready", StringComparison.Ordinal),
            "A failed support check still reported readiness on standard output.");
        fixture.AssertLogRecordsStartupAndExit();
        Assert(
            File.Exists(fixture.EntryPoint),
            "The readiness check removed or replaced the fixture entry point.");
    }

    private static async Task JsonFailureIsStructuredAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["setup", "--json"]).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        using JsonDocument document = JsonDocument.Parse(fixture.Output.ToString());
        JsonElement root = document.RootElement;
        Assert(!root.GetProperty("ok").GetBoolean(), "The JSON failure reported success.");
        Assert(
            root.GetProperty("schemaVersion").GetInt32() == 1,
            "The JSON failure did not report schema version 1.");
        Assert(
            root.GetProperty("error").GetProperty("type").GetString() == "cli_error",
            "The JSON failure did not use the cli_error envelope.");
    }

    // Spectre.Console composes the renderables; this proves the composition,
    // the ANSI writer, and the no-colour writer all survive trimming and
    // ahead-of-time compilation, and that the two stay textually identical.
    private static Task SpectreOutputRenders()
    {
        var result = new SetupCommandResult(
            0,
            @"C:\package\app",
            "24.20.0",
            new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "ready",
                Changed: true),
            SessionReady: true,
            RuntimeLocation: SetupRuntimeLocation.IsolatedSession);

        using var colored = new StringWriter();
        using var plain = new StringWriter();
        ClawCtlConsole.WriteResult(colored, result, useColor: true);
        ClawCtlConsole.WriteResult(plain, result);

        string coloredText = colored.ToString();
        Assert(
            coloredText.Contains('\u001b', StringComparison.Ordinal),
            "Spectre did not emit an ANSI color sequence.");
        Assert(
            !plain.ToString().Contains('\u001b', StringComparison.Ordinal),
            "The plain renderer leaked an ANSI color sequence.");
        Assert(
            StripAnsi(coloredText) == plain.ToString(),
            "Colored output did not match plain output once ANSI was stripped.");

        using var failure = new StringWriter();
        ClawCtlConsole.WriteUnexpectedFailure(failure, "setup", "no package identity.");
        Assert(
            failure.ToString().Contains("no package identity.", StringComparison.Ordinal),
            "The note callout did not render its message.");

        using var hint = new StringWriter();
        ClawCtlConsole.WriteGatewayHint(
            hint,
            useColor: true,
            useUnicode: true);
        string visibleHint = StripAnsi(hint.ToString());
        Assert(
            visibleHint.Contains("\U0001f980 Hint:", StringComparison.Ordinal) &&
            visibleHint.Contains(
                "Run clawctl gateway-service start",
                StringComparison.Ordinal) &&
            !visibleHint.Contains(
                "'clawctl gateway-service start'",
                StringComparison.Ordinal),
            "The colored gateway hint lost its branding or command formatting.");

        using var readinessJson = new StringWriter();
        ClawCtlJson.WriteResult(
            readinessJson,
            new GatewayCommandResult(
                "status",
                GatewayState.NotStarted,
                "No gateway has been started.",
                null,
                0,
                Readiness: new AgentConfigReadinessStatus(
                    AgentConfigReadinessState.StartupEligible)));
        using JsonDocument readinessDocument =
            JsonDocument.Parse(readinessJson.ToString());
        Assert(
            readinessDocument.RootElement
                .GetProperty("gateway")
                .GetProperty("readiness")
                .GetProperty("state")
                .GetString() == "startup-eligible",
            "The readiness JSON projection did not survive NativeAOT.");
        return Task.CompletedTask;
    }

    // Spectre's live displays and the generic progress plumbing are the parts
    // most likely to depend on something trimming removes, and narration only
    // ever runs on a real start, which no unit test performs end to end.
    private static async Task GatewayNarrationRenders()
    {
        using var narrated = new StringWriter();
        int value = await ClawCtlConsole.NarrateAsync(
            narrated,
            useColor: false,
            narrate: true,
            GatewayStartProgress.Initial,
            progress =>
            {
                progress.Report(new GatewayStartProgress(
                    GatewayStartStage.WaitingForListener,
                    "Waiting for the gateway to start listening."));
                return Task.FromResult(42);
            }).ConfigureAwait(false);

        Assert(value == 42, "Narration did not return the operation's result.");
        Assert(
            narrated.ToString().Contains("start listening", StringComparison.Ordinal),
            "Narration did not report its stage.");

        using var silent = new StringWriter();
        await ClawCtlConsole.NarrateAsync(
            silent,
            useColor: false,
            narrate: false,
            GatewayStartProgress.Initial,
            progress =>
            {
                progress.Report(new GatewayStartProgress(
                    GatewayStartStage.Launching,
                    "Launching the gateway."));
                return Task.FromResult(0);
            }).ConfigureAwait(false);

        Assert(
            silent.ToString().Length == 0,
            "Narration wrote output when it was turned off.");

        using var result = new StringWriter();
        ClawCtlConsole.WriteResult(result, new GatewayCommandResult(
            "start",
            GatewayState.Running,
            "The gateway is running on port 18789.",
            null,
            0,
            18789,
            "http://127.0.0.1:18789/"));
        Assert(
            result.ToString().Contains("http://127.0.0.1:18789/", StringComparison.Ordinal),
            "The gateway result did not report its URL.");
    }

    private static string StripAnsi(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '\u001b')
            {
                builder.Append(value[index]);
                continue;
            }

            while (index < value.Length && value[index] != 'm')
            {
                index++;
            }
        }

        return builder.ToString();
    }

    // The operational error boundary is part of startup, not of the parser.
    // It directs users to the diagnostics command instead of exposing an
    // implementation-specific log path.
    private static async Task MissingApplicationReportsAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();

        int exitCode = await fixture.RunAsync(["setup"]).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        AssertContains(fixture.Error.ToString(), "clawctl collect-logs", fixture);
        Assert(
            !fixture.Output.ToString().Contains("package is ready", StringComparison.Ordinal),
            "A failed readiness check still reported success.");
        fixture.AssertLogRecordsStartupAndExit();
    }

    // The agent entrypoint must never consult System.CommandLine. These tokens
    // are all meaningful to the clawctl parser, so if the parser ever saw them
    // this scenario would render help and exit zero.
    private static async Task AgentNeverParsesItsArgumentsAsync()
    {
        string[] arguments =
            ["--help", "--version", "--", "@response.rsp", "[suggest:1]", "", "a b"];
        using Fixture fixture = Fixture.CreateWithoutApplication(HostEntrypoint.Agent);

        int exitCode = await fixture.RunAsync(arguments).ConfigureAwait(false);

        AssertExitCode(1, exitCode, fixture);
        Assert(
            fixture.Output.ToString().Length == 0,
            "The agent entrypoint rendered clawctl output for forwarded arguments.");
        AssertContains(fixture.Error.ToString(), fixture.LogPath, fixture);
        fixture.AssertLogRecordsStartupAndExit();
    }

    private static string LauncherVersion() =>
        typeof(HostStartup).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string FlattenRendered(string value) =>
        string.Join(' ', value.Replace('|', ' ').Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));

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
        private readonly HostEntrypoint _entrypoint;
        private readonly WorkTrackingLifecycle? _lifecycle;

        private Fixture(string root, HostEntrypoint entrypoint, bool allowInstallationWork)
        {
            Root = root;
            _entrypoint = entrypoint;
            _lifecycle = allowInstallationWork ? null : new WorkTrackingLifecycle();
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

        public static Fixture CreateWithoutApplication(
            HostEntrypoint entrypoint = HostEntrypoint.Control) =>
            new(CreateRoot(), entrypoint, allowInstallationWork: false);

        // Scenarios that must reach the production support check opt in; every
        // other scenario gets a lifecycle that refuses to do real work.
        public static async Task<Fixture> CreateWithApplicationAsync(
            HostEntrypoint entrypoint = HostEntrypoint.Control,
            bool allowInstallationWork = false)
        {
            Fixture fixture = new(CreateRoot(), entrypoint, allowInstallationWork);
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
                InstallationLifecycle =
                    _lifecycle ?? (IInstallationLifecycle)InstallationLifecycle.Production
            };

            return await LauncherProgram.RunAsync(args, startup).ConfigureAwait(false);
        }

        public void AssertNoInstallationWorkStarted() =>
            Assert(
                _lifecycle is not null && !_lifecycle.WorkStarted,
                "Installation work ran for an invocation that must not start it.");

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

        // Records whether the command reached real installation work, and
        // refuses to perform any. Help, version, rejected input, and a missing
        // application must all fail or finish before touching this.
        private sealed class WorkTrackingLifecycle : IInstallationLifecycle
        {
            public bool WorkStarted { get; private set; }

            private InvalidOperationException Started()
            {
                WorkStarted = true;
                return new InvalidOperationException(
                    "The scenario driver never performs installation work.");
            }

            public SessionRuntime CreateRuntime(Action<string> log) => throw Started();

            public Task EnsureSessionSupportedAsync(CancellationToken cancellationToken) =>
                throw Started();

            public PackageRuntimeMetadata ValidatePackageRuntime(
                HostOptions options,
                SessionRuntime sessionRuntime) => throw Started();

            public ISessionLockHandle AcquireLifecycleLock(SessionRuntime sessionRuntime) =>
                throw Started();

            public Task<TeardownResult> TeardownAsync(
                HostOptions options,
                SessionRuntime sessionRuntime,
                Action<string> log,
                bool lockAlreadyHeld,
                CancellationToken cancellationToken) => throw Started();

            public IInstallationStateCleaner CreateStateCleaner(
                SessionRuntime sessionRuntime) => throw Started();

            public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
                Action<string> log,
                CancellationToken cancellationToken) => throw Started();

            public Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
                Action<string> log,
                CancellationToken cancellationToken) => throw Started();
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
