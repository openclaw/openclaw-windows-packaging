using System.CommandLine;
using System.Reflection;

namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlCommandLineTests
{
    // Any invocation that reaches this resolver has started the readiness
    // operation, which help, version, and rejected input must never do.
    private static Task<NodeRuntime> FailIfSetupRuns(CancellationToken _) =>
        throw new InvalidOperationException(
            "Setup ran for an invocation that should not have started it.");

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []),
            args,
            _ => { },
            output,
            error,
            FailIfSetupRuns).ConfigureAwait(false);

        return (exitCode, output.ToString(), error.ToString());
    }

    // Generated help wraps to the terminal width, so assert on content rather
    // than layout. Pinning a width would make the tests pass while real users
    // saw different output.
    private static string Normalize(string text) =>
        string.Join(
            ' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [InlineData()]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public async Task DiscoveryInputPrintsHelpAndSucceeds(params string[] args)
    {
        (int exitCode, string output, string error) = await RunAsync(args).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Contains("setup", Normalize(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpDescribesBundledRuntimePreparation()
    {
        (_, string output, _) = await RunAsync("--help").ConfigureAwait(true);
        string help = Normalize(output);

        Assert.Contains("setup", help, StringComparison.Ordinal);
        Assert.Contains("--version", help, StringComparison.Ordinal);
        Assert.Contains("bundled Node.js", help, StringComparison.Ordinal);
        Assert.Contains("clawctl setup", help, StringComparison.Ordinal);
        Assert.Contains("openclaw <arguments>", help, StringComparison.Ordinal);
    }

    // clawctl owns package readiness and the managed gateway only. Upstream
    // command names must continue flowing through `openclaw` unchanged.
    [Fact]
    public void OnlyTheOwnedCommandsAreExposed()
    {
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = _ => Task.FromResult(0),
            GatewayStart = _ => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        });

        Assert.Equal(
            [
                ClawCtlCommandLine.SetupCommandName,
                ClawCtlCommandLine.StatusCommandName,
                ClawCtlCommandLine.CollectLogsCommandName,
                "teardown",
                "pwsh",
                "gateway-service"
            ],
            root.Subcommands.Select(command => command.Name));
    }

    [Fact]
    public async Task GatewayServiceStartInvokesOnlyTheStartHandler()
    {
        int starts = 0;
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = _ => Task.FromResult(0),
            GatewayStart = _ =>
            {
                starts++;
                return Task.FromResult(0);
            },
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        });

        int exitCode = await root.Parse("gateway-service start").InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task SetupHelpDescribesRuntimePreparationWithoutRunningIt()
    {
        (int exitCode, string output, string error) =
            await RunAsync("setup", "--help").ConfigureAwait(true);
        string help = Normalize(output);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Contains("setup", help, StringComparison.Ordinal);
        Assert.Contains(
            Normalize(ClawCtlCommandLine.SetupDescription),
            help,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupFreshPassesTheExplicitDestructiveAuthorization()
    {
        SetupOptions? received = null;
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (options, _) =>
            {
                received = options;
                return Task.FromResult(0);
            },
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = _ => Task.FromResult(0),
            GatewayStart = _ => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        });

        int exitCode = await root.Parse("setup --fresh").InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(new SetupOptions(Fresh: true), received);
    }

    [Fact]
    public async Task PowerShellHelpDescribesTheIsolatedAgentShell()
    {
        (int exitCode, string output, string error) =
            await RunAsync("pwsh", "--help").ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Contains("isolated agent", Normalize(output), StringComparison.Ordinal);
    }

    // The built-in version action reports the entry assembly, which under a test
    // host or scenario runner is not the launcher. Assert the launcher's own
    // version so that substitution is caught.
    [Fact]
    public async Task VersionReportsTheLauncherAssemblyVersion()
    {
        string expected = typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        (int exitCode, string output, string error) = await RunAsync("--version").ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Equal(expected, output.Trim());
        Assert.NotEqual(
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            output.Trim());
    }

    // The old parser rejected `--version` combined with anything else. The
    // library's version action clears parse errors instead, so trailing
    // garbage is ignored. This is a deliberate consequence of adopting the
    // standard UX, recorded here so the change is visible rather than
    // discovered by a user.
    [Fact]
    public async Task VersionWinsOverTrailingArguments()
    {
        (int exitCode, string output, _) =
            await RunAsync("--version", "bogus").ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            typeof(Program).Assembly.GetName().Version?.ToString(),
            output.Trim());
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("--bogus")]
    [InlineData("prepare")]
    [InlineData("verify")]
    [InlineData("repair")]
    [InlineData("update-package")]
    [InlineData("gateway-service")]
    [InlineData("setup", "extra")]
    [InlineData("setup", "--bogus")]
    public async Task RejectedInputFailsWithoutStartingSetup(params string[] args)
    {
        (int exitCode, string output, string error) = await RunAsync(args).ConfigureAwait(true);

        Assert.Equal(1, exitCode);
        Assert.NotEmpty(error);
        Assert.DoesNotContain(
            "package is ready",
            output,
            StringComparison.Ordinal);
    }
}
