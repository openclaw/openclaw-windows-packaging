using System.CommandLine;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlCommandLineTests
{
    private const string OpenClawCompletionScript =
        "Register-ArgumentCompleter -Native -CommandName openclaw -ScriptBlock {}";

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
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
        });

        Assert.Equal(
            [
                ClawCtlCommandLine.SetupCommandName,
                ClawCtlCommandLine.StatusCommandName,
                ClawCtlCommandLine.CollectLogsCommandName,
                "teardown",
                ClawCtlCommandLine.OpenCommandName,
                ClawCtlCommandLine.CompletionCommandName,
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
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) =>
            {
                starts++;
                return Task.FromResult(0);
            },
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
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
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
        }, outputOptions);

        int exitCode = await root.Parse("open --json --no-color").InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(1, opens);
        Assert.True(outputOptions.Json);
        Assert.True(outputOptions.NoColor);
    }

    [Fact]
    public async Task GatewayServiceRestartInvokesOnlyTheRestartHandler()
    {
        int restarts = 0;
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ =>
            {
                restarts++;
                return Task.FromResult(0);
            }
        });

        int exitCode = await root.Parse("gateway-service restart").InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task GatewayServiceRestartRejectsTheRecoveryMarker()
    {
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
        });

        int exitCode = await root
            .Parse("gateway-service restart --recovery")
            .InvokeAsync();

        Assert.Equal(1, exitCode);
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
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (value, _) =>
            {
                recovery = value;
                return Task.FromResult(0);
            },
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
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
    [InlineData("open --json")]
    [InlineData("completion --json")]
    [InlineData("--json gateway-service status")]
    [InlineData("gateway-service start --json")]
    [InlineData("gateway-service --json start")]
    [InlineData("gateway-service status --json")]
    [InlineData("gateway-service --json status")]
    [InlineData("gateway-service stop --json")]
    [InlineData("gateway-service --json stop")]
    [InlineData("gateway-service restart --json")]
    [InlineData("gateway-service --json restart")]
    public async Task JsonIsAvailableToEveryNonInteractiveCommand(string commandLine)
    {
        var outputOptions = new ClawCtlOutputOptions();
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            Open = _ => Task.FromResult(0),
            Completion = (_, _) => Task.FromResult(0),
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
        }, outputOptions);

        int exitCode = await root.Parse(commandLine).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.True(outputOptions.Json);
    }

    [Theory]
    [InlineData("pwsh --json")]
    [InlineData("--json pwsh")]
    public async Task JsonIsRejectedForInteractivePowerShell(string commandLine)
    {
        (int exitCode, _, string error) =
            await RunAsync(commandLine.Split(' ')).ConfigureAwait(true);

        Assert.Equal(1, exitCode);
        Assert.Contains("--json", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerShellCommandIsDeliveredAsOneString()
    {
        PowerShellOptions? captured = null;
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = (options, _) =>
            {
                captured = options;
                return Task.FromResult(0);
            },
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
        });
        const string command = "Get-Content ~/foo.txt; Write-Output '%PATH%'";

        int exitCode = await root.Parse(
            ["pwsh", "--command", command]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal(command, captured.Command);
        Assert.Null(captured.File);
        Assert.Empty(captured.Arguments);
    }

    [Fact]
    public async Task PowerShellFileArgumentsAreDeliveredWithoutReparsing()
    {
        PowerShellOptions? captured = null;
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = (options, _) =>
            {
                captured = options;
                return Task.FromResult(0);
            },
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
        });

        int exitCode = await root.Parse(
            [
                "pwsh",
                "--file",
                @".\scripts\diagnose.ps1",
                "--",
                "--name",
                "hello world",
                "%PATH%"
            ]).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Null(captured.Command);
        Assert.Equal(@".\scripts\diagnose.ps1", captured.File);
        Assert.Equal(["--name", "hello world", "%PATH%"], captured.Arguments);
    }

    [Theory]
    [InlineData("pwsh --command one --file two.ps1")]
    [InlineData("pwsh --command one unexpected")]
    [InlineData("pwsh unexpected")]
    public async Task InvalidPowerShellModeDoesNotStartWork(string commandLine)
    {
        (int exitCode, _, string error) =
            await RunAsync(commandLine.Split(' ')).ConfigureAwait(true);

        Assert.Equal(1, exitCode);
        Assert.NotEmpty(error);
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
    [InlineData("gateway-service restart --no-color")]
    public async Task NoColorIsAvailableToEveryCommand(string commandLine)
    {
        var outputOptions = new ClawCtlOutputOptions();
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
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
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
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
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
        });

        int exitCode = await root.Parse("setup --fresh --force").InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(new SetupOptions(Fresh: true, Force: true), received);
    }

    [Fact]
    public async Task CompletionInstallDispatchesProfileOptions()
    {
        CompletionOptions? received = null;
        RootCommand root = ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = (_, _) => Task.FromResult(0),
            Status = _ => Task.FromResult(0),
            CollectLogs = (_, _) => Task.FromResult(0),
            Teardown = (_, _) => Task.FromResult(0),
            Completion = (value, _) =>
            {
                received = value;
                return Task.FromResult(0);
            },
            PowerShell = (_, _) => Task.FromResult(0),
            GatewayStart = (_, _) => Task.FromResult(0),
            GatewayStatus = _ => Task.FromResult(0),
            GatewayStop = _ => Task.FromResult(0),
            GatewayRestart = _ => Task.FromResult(0)
        });

        int exitCode = await root.Parse("completion --install --profile C:\\test\\profile.ps1").InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(
            new CompletionOptions(
                Install: true,
                Uninstall: false,
                ProfilePath: @"C:\test\profile.ps1"),
            received);
    }

    [Fact]
    public async Task CompletionRejectsConflictingProfileOperations()
    {
        (int exitCode, _, string error) =
            await RunAsync("completion", "--install", "--uninstall").ConfigureAwait(true);

        Assert.Equal(1, exitCode);
        Assert.Contains("cannot be used together", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionProfileInstallAndUninstallPreserveOtherProfileContent()
    {
        string directory = TestDirectory.Create();
        string profile = Path.Combine(directory, "profile.ps1");
        try
        {
            File.WriteAllText(profile, "Set-StrictMode -Version Latest\r\n");

            PowerShellCompletion.Install(profile);

            string installed = File.ReadAllText(profile);
            Assert.Contains("Set-StrictMode -Version Latest", installed, StringComparison.Ordinal);
            Assert.Contains(PowerShellCompletion.BeginMarker, installed, StringComparison.Ordinal);
            Assert.Contains(
                "Get-Command clawctl -CommandType Application",
                installed,
                StringComparison.Ordinal);
            Assert.Contains(
                "& $clawctlCommand.Source completion",
                installed,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                OpenClawCompletionScript,
                installed,
                StringComparison.Ordinal);

            PowerShellCompletion.Uninstall(profile);

            string uninstalled = File.ReadAllText(profile);
            Assert.Equal("Set-StrictMode -Version Latest" + Environment.NewLine, uninstalled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompletionProfileRelativePathIsResolved()
    {
        string directory = TestDirectory.Create();
        string profile = Path.Combine(directory, "profile.ps1");
        try
        {
            string installedProfile = PowerShellCompletion.Install(
                "profile.ps1",
                directory);

            Assert.Equal(Path.GetFullPath(profile), installedProfile);
            Assert.True(File.Exists(profile));

            string uninstalledProfile = PowerShellCompletion.Uninstall(
                "profile.ps1",
                directory);

            Assert.Equal(installedProfile, uninstalledProfile);
            Assert.Empty(File.ReadAllText(profile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompletionProfileUpdatePreservesUnrelatedBytes()
    {
        string directory = TestDirectory.Create();
        string profile = Path.Combine(directory, "profile.ps1");
        byte[] original = Encoding.UTF8.GetBytes("Write-Host 'keep'  \r\n \t");
        try
        {
            File.WriteAllBytes(profile, original);

            PowerShellCompletion.Install(profile);
            PowerShellCompletion.Uninstall(profile);

            Assert.Equal(original, File.ReadAllBytes(profile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompletionProfileUninstallSeparatesSurvivingCommands()
    {
        string directory = TestDirectory.Create();
        string profile = Path.Combine(directory, "profile.ps1");
        try
        {
            File.WriteAllText(profile, "Write-Host 'before'");

            PowerShellCompletion.Install(profile);
            File.AppendAllText(profile, "Write-Host 'after'");
            PowerShellCompletion.Uninstall(profile);

            Assert.Equal(
                "Write-Host 'before'\nWrite-Host 'after'",
                File.ReadAllText(profile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionProfilePreservesUtf32Encoding(bool bigEndian)
    {
        string directory = TestDirectory.Create();
        string profile = Path.Combine(directory, "profile.ps1");
        var encoding = new UTF32Encoding(
            bigEndian,
            byteOrderMark: true,
            throwOnInvalidCharacters: true);
        byte[] original =
        [
            .. encoding.GetPreamble(),
            .. encoding.GetBytes("Set-StrictMode -Version Latest\r\n")
        ];
        try
        {
            File.WriteAllBytes(profile, original);

            PowerShellCompletion.Install(profile);

            byte[] installed = File.ReadAllBytes(profile);
            Assert.True(installed.AsSpan().StartsWith(encoding.GetPreamble()));
            Assert.Contains(
                PowerShellCompletion.BeginMarker,
                encoding.GetString(installed, encoding.GetPreamble().Length,
                    installed.Length - encoding.GetPreamble().Length),
                StringComparison.Ordinal);

            PowerShellCompletion.Uninstall(profile);

            Assert.Equal(original, File.ReadAllBytes(profile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompletionCacheRefreshesOnlyWhenInstalled()
    {
        string directory = TestDirectory.Create();
        string cache = Path.Combine(directory, "openclaw.ps1");
        try
        {
            Assert.False(PowerShellCompletion.SynchronizeCacheIfInstalled(
                cache,
                OpenClawCompletionScript));
            Assert.False(File.Exists(cache));

            File.WriteAllText(cache, "stale");

            Assert.True(PowerShellCompletion.SynchronizeCacheIfInstalled(
                cache,
                OpenClawCompletionScript));
            Assert.Equal(OpenClawCompletionScript, File.ReadAllText(cache));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
