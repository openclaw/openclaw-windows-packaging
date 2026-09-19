using System.CommandLine;
using System.Text.Json;
using System.Reflection;

namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlCommandLineTests
{
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
            new FailIfWorkStartsLifecycle()).ConfigureAwait(false);

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
    [InlineData(1, "status", "--no-color=invalid")]
    [InlineData(0, "status", "--help", "--no-color=invalid")]
    public async Task MalformedNoColorStillRendersOrdinaryHelp(
        int expectedExitCode,
        params string[] args)
    {
        (int exitCode, string output, string error) =
            await RunAsync(args).ConfigureAwait(true);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("This shouldn't happen", error, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[", output, StringComparison.Ordinal);
    }

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
        Assert.Contains("ready", help, StringComparison.Ordinal);
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
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        });

        Assert.Equal(
            [
                ClawCtlCommandLine.SetupCommandName,
                ClawCtlCommandLine.StatusCommandName,
                ClawCtlCommandLine.CollectLogsCommandName,
                "teardown",
                ClawCtlCommandLine.OpenCommandName,
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
            GatewayStart = (_, _) =>
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
    public async Task OpenInvokesItsHandlerAndInheritsOutputOptions()
    {
        int opens = 0;
        var outputOptions = new ClawCtlOutputOptions();
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            Open = _ =>
            {
                opens++;
                return Task.FromResult(0);
            },
            PowerShell = _ => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        }, outputOptions);

        int exitCode = await root.Parse("open --json --no-color").InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(1, opens);
        Assert.True(outputOptions.Json);
        Assert.True(outputOptions.NoColor);
    }

    [Theory]
    [InlineData("gateway-service start", false)]
    [InlineData("gateway-service start --recovery", true)]
    public async Task GatewayServiceStartReportsRecoveryProvenance(
        string commandLine,
        bool expectedRecovery)
    {
        bool? recovery = null;
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = _ => Task.FromResult(0),
            GatewayStart = (value, _) =>
            {
                recovery = value;
                return Task.FromResult(0);
            },
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        });

        int exitCode = await root.Parse(commandLine).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(expectedRecovery, recovery);
    }

    [Theory]
    [InlineData("setup --json")]
    [InlineData("--json status")]
    [InlineData("status --json")]
    [InlineData("collect-logs --json")]
    [InlineData("teardown --json")]
    [InlineData("gateway-service start --json")]
    [InlineData("gateway-service status --json")]
    [InlineData("gateway-service stop --json")]
    public async Task JsonIsAvailableToEveryNonInteractiveCommand(string commandLine)
    {
        var outputOptions = new ClawCtlOutputOptions();
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = _ => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        }, outputOptions);

        int exitCode = await root.Parse(commandLine).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.True(outputOptions.Json);
    }

    [Fact]
    public async Task JsonIsRejectedForInteractivePowerShell()
    {
        (int exitCode, _, string error) =
            await RunAsync("pwsh", "--json").ConfigureAwait(true);

        Assert.Equal(1, exitCode);
        Assert.Contains(
            "'--json' is not supported for 'pwsh'",
            error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("setup --no-color")]
    [InlineData("--no-color status")]
    [InlineData("status --no-color")]
    [InlineData("collect-logs --no-color")]
    [InlineData("teardown --no-color")]
    [InlineData("pwsh --no-color")]
    [InlineData("gateway-service start --no-color")]
    [InlineData("gateway-service status --no-color")]
    [InlineData("gateway-service stop --no-color")]
    public async Task NoColorIsAvailableToEveryCommand(string commandLine)
    {
        var outputOptions = new ClawCtlOutputOptions();
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = _ => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        }, outputOptions);

        int exitCode = await root.Parse(commandLine).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.True(outputOptions.NoColor);
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
        Assert.Contains("--fresh", help, StringComparison.Ordinal);
        Assert.Contains("Requires --fresh", help, StringComparison.Ordinal);
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
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        });

        int exitCode = await root.Parse("setup --fresh").InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(new SetupOptions(Fresh: true, Force: false), received);
    }

    [Fact]
    public async Task SetupFreshForcePassesTheExplicitRecoveryOverride()
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
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0)
        });

        int exitCode = await root.Parse("setup --fresh --force").InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(new SetupOptions(Fresh: true, Force: true), received);
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
    // host or scenario runner is not the launcher. Assert the build identity
    // compiled into the launcher so that substitution is caught.
    [Fact]
    public async Task VersionReportsTheBakedBuildIdentity()
    {
        (int exitCode, string output, string error) = await RunAsync("--version").ConfigureAwait(true);
        string reported = Normalize(output);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Contains(ClawCtlBuildMetadata.PackageVersion, reported, StringComparison.Ordinal);
        Assert.Contains(ClawCtlBuildMetadata.PackageCommit, reported, StringComparison.Ordinal);
        Assert.Contains(ClawCtlBuildMetadata.PayloadVersion, reported, StringComparison.Ordinal);
        Assert.Contains(ClawCtlBuildMetadata.PayloadCommit, reported, StringComparison.Ordinal);

        // The defect this guards: falling back to the library's action, which
        // reports whichever assembly started the process.
        string? entryVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        if (entryVersion is not null)
        {
            Assert.NotEqual(entryVersion, reported);
        }
    }

    // --version is satisfied by the version option before command dispatch, so
    // it is the one place a JSON document is produced without a command result.
    // The documented contract is that every non-interactive command supports
    // --json, and silently ignoring it would break a caller that piped it.
    [Fact]
    public async Task VersionJsonReportsTheBuildIdentity()
    {
        (int exitCode, string output, string error) =
            await RunAsync("--version", "--json").ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("version", root.GetProperty("command").GetString());

        JsonElement package = root.GetProperty("package");
        Assert.Equal(
            ClawCtlBuildMetadata.PackageVersion,
            package.GetProperty("version").GetString());
        Assert.Equal(
            ClawCtlBuildMetadata.PackageCommit,
            package.GetProperty("commit").GetString());

        JsonElement payload = root.GetProperty("payload");
        Assert.Equal(
            ClawCtlBuildMetadata.PayloadVersion,
            payload.GetProperty("version").GetString());
        Assert.Equal(
            ClawCtlBuildMetadata.PayloadCommit,
            payload.GetProperty("commit").GetString());
    }

    [Theory]
    [InlineData("--json=invalid")]
    [InlineData("--no-color=invalid")]
    public async Task VersionPrecedenceSurvivesMalformedBooleanOptions(string option)
    {
        (int exitCode, string output, string error) =
            await RunAsync("--version", option).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Contains(ClawCtlBuildMetadata.PackageVersion, output, StringComparison.Ordinal);
        Assert.DoesNotContain("This shouldn't happen", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionJsonIsAcceptedBeforeTheVersionOption()
    {
        (int exitCode, string output, _) =
            await RunAsync("--json", "--version").ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("version", document.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task VersionPairsEachCommitWithItsVersion()
    {
        (_, string output, _) = await RunAsync("--version").ConfigureAwait(true);
        string reported = Normalize(output);

        Assert.Contains("Package:", reported, StringComparison.Ordinal);
        Assert.Contains("Payload:", reported, StringComparison.Ordinal);

        // Each commit belongs to the version it sits behind, so assert the
        // pairing rather than the mere presence of four strings.
        Assert.Contains(
            $"Package: {ClawCtlBuildMetadata.PackageVersion} ({ClawCtlBuildMetadata.PackageCommit})",
            reported,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Payload: {ClawCtlBuildMetadata.PayloadVersion} ({ClawCtlBuildMetadata.PayloadCommit})",
            reported,
            StringComparison.Ordinal);
    }

    // The commit is de-emphasised relative to the version it qualifies, so the
    // two must not render in the same style.
    [Fact]
    public void VersionStylesTheCommitApartFromTheVersion()
    {
        using var colored = new StringWriter();
        ClawCtlConsole.WriteVersion(colored, useColor: true);
        string text = colored.ToString();

        int versionIndex = text.IndexOf(
            ClawCtlBuildMetadata.PackageVersion,
            StringComparison.Ordinal);
        int commitIndex = text.IndexOf(
            ClawCtlBuildMetadata.PackageCommit,
            StringComparison.Ordinal);

        Assert.True(versionIndex >= 0 && commitIndex > versionIndex);
        Assert.Contains(
            "\u001b[",
            text[versionIndex..commitIndex],
            StringComparison.Ordinal);
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
        Assert.Contains(
            ClawCtlBuildMetadata.PackageVersion,
            Normalize(output),
            StringComparison.Ordinal);
        Assert.DoesNotContain("bogus", output, StringComparison.Ordinal);
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
    [InlineData("setup", "--force")]
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
