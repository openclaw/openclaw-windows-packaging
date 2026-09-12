using System.CommandLine;
using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// Drives the real clawctl command tree, so routing and refusal are proven by
/// which operation actually ran rather than by a parser's intermediate result.
/// </summary>
public sealed class ClawCtlGatewayCommandParsingTests
{
    private readonly List<string> _invoked = [];

    private RootCommand CreateCommand() =>
        ClawCtlCommandLine.Create(new ClawCtlHandlers
        {
            Setup = Record("setup"),
            SessionStatus = Record("session:status"),
            SessionStop = Record("session:stop"),
            SessionRemove = Record("session:remove"),
            GatewayInstall = Record("gateway:install"),
            GatewayStatus = Record("gateway:status"),
            GatewayStart = Record("gateway:start"),
            GatewayStop = Record("gateway:stop"),
            GatewayUninstall = Record("gateway:uninstall"),
            GatewayDiagnose = Record("gateway:diagnose")
        });

    private Func<CancellationToken, Task<int>> Record(string name) =>
        _ =>
        {
            _invoked.Add(name);
            return Task.FromResult(0);
        };

    private (int ExitCode, string Output, string Error) Invoke(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CreateCommand()
            .Parse(args, ClawCtlCommandLine.CreateParserConfiguration())
            .Invoke(new InvocationConfiguration
            {
                Output = output,
                Error = error,
                EnableDefaultExceptionHandler = false
            });
        return (exitCode, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("install", "gateway:install")]
    [InlineData("status", "gateway:status")]
    [InlineData("start", "gateway:start")]
    [InlineData("stop", "gateway:stop")]
    [InlineData("uninstall", "gateway:uninstall")]
    [InlineData("diagnose", "gateway:diagnose")]
    public void EachSubCommandRunsItsOwnOperation(string verb, string expected)
    {
        (int exitCode, _, _) = Invoke("gateway-service", verb);

        Assert.Equal(0, exitCode);
        Assert.Equal([expected], _invoked);
    }

    [Fact]
    public void TheNounIsGatewayServiceSoItCannotShadowOpenClawsOwnGatewayCommand()
    {
        // `openclaw gateway run` belongs to OpenClaw. A host `gateway` noun
        // would shadow it.
        (int exitCode, _, string error) = Invoke("gateway", "status");

        Assert.NotEqual(0, exitCode);
        Assert.Empty(_invoked);
        Assert.Contains("gateway", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABareNounShowsItsSubCommandsAndRunsNothing()
    {
        (int exitCode, string output, _) = Invoke("gateway-service");

        Assert.Equal(0, exitCode);
        Assert.Empty(_invoked);
        foreach (string verb in
            new[] { "install", "status", "start", "stop", "uninstall", "diagnose" })
        {
            Assert.Contains(verb, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AMistypedVerbRunsNothing()
    {
        // A mistyped destructive verb must never resolve to a different
        // operation than the user typed.
        (int exitCode, _, _) = Invoke("gateway-service", "unistall");

        Assert.NotEqual(0, exitCode);
        Assert.Empty(_invoked);
    }

    [Fact]
    public void UnexpectedArgumentsRunNothing()
    {
        (int exitCode, _, _) = Invoke("gateway-service", "start", "--port", "9000");

        Assert.NotEqual(0, exitCode);
        Assert.Empty(_invoked);
    }

    [Theory]
    [InlineData("status", "session:status")]
    [InlineData("stop", "session:stop")]
    [InlineData("remove", "session:remove")]
    public void SessionSubCommandsRunTheirOwnOperation(string verb, string expected)
    {
        (int exitCode, _, _) = Invoke("session", verb);

        Assert.Equal(0, exitCode);
        Assert.Equal([expected], _invoked);
    }

    [Fact]
    public void RootHelpNamesTheManagementCommands()
    {
        // A feature nothing mentions cannot be discovered.
        (int exitCode, string output, _) = Invoke("--help");

        Assert.Equal(0, exitCode);
        Assert.Empty(_invoked);
        foreach (string noun in new[] { "setup", "session", "gateway-service" })
        {
            Assert.Contains(noun, output, StringComparison.Ordinal);
        }
    }
}
public sealed class ClawCtlGatewayConsoleTests
{
    private static GatewayRecord Record() => new()
    {
        SandboxId = "sandbox-1",
        ProcessId = 42,
        ProcessStartTimeUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        Port = 4517,
        LogPath = @"C:\state\gateway.log",
        StartedUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private static string Render(
        GatewayStatusReport report,
        GatewayPersistenceStatus? persistence = null)
    {
        using var writer = new StringWriter();
        ClawCtlConsole.WriteGatewayStatus(writer, report, persistence);
        return writer.ToString();
    }

    [Fact]
    public void NotStartedTellsTheUserHowToStart()
    {
        string text = Render(
            new GatewayStatusReport(GatewayState.NotStarted, null, "none"));

        Assert.Contains("gateway-service install", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownIsNotPresentedAsStopped()
    {
        // A user told the gateway is stopped will start another one, which is
        // exactly the wrong move when nothing could be observed.
        string text = Render(
            new GatewayStatusReport(
                GatewayState.Unknown,
                Record(),
                "unknown",
                "the session did not answer"));

        Assert.Contains("could not be determined", text, StringComparison.Ordinal);
        Assert.Contains("not the same as stopped", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "gateway-service start",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StoppedOffersTheStartCommand()
    {
        string text = Render(
            new GatewayStatusReport(GatewayState.Stopped, Record(), "stopped"));

        Assert.Contains("gateway-service start", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnhealthyPointsAtTheLog()
    {
        string text = Render(
            new GatewayStatusReport(
                GatewayState.Unhealthy,
                Record(),
                "unhealthy",
                "not listening"));

        Assert.Contains(@"C:\state\gateway.log", text, StringComparison.Ordinal);
        Assert.Contains("not listening", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DriftIsReportedWithTheCommandThatRepairsIt()
    {
        string text = Render(
            new GatewayStatusReport(GatewayState.Running, Record(), "running"),
            new GatewayPersistenceStatus(
                GatewayPersistenceState.ActionRequired,
                GatewayPersistenceLane.TaskScheduler,
                "drifted",
                "the task runs a different command",
                GatewayPersistenceManager.RepairCommand));

        Assert.Contains("needs attention", text, StringComparison.Ordinal);
        Assert.Contains(
            GatewayPersistenceManager.RepairCommand,
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AHealthyInstallationIsNotToldToRepairItself()
    {
        string text = Render(
            new GatewayStatusReport(GatewayState.Running, Record(), "running"),
            new GatewayPersistenceStatus(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "ready",
                null,
                GatewayPersistenceManager.RepairCommand));

        Assert.Contains("configured", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Repair with", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartupFolderLaneIsNamedRatherThanHidden()
    {
        string text = Render(
            new GatewayStatusReport(GatewayState.Running, Record(), "running"),
            new GatewayPersistenceStatus(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.StartupFolderFallback,
                "ready",
                "the task could not be registered"));

        Assert.Contains("Startup folder", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitAutostartChoiceIsExplainedOnStart()
    {
        using var writer = new StringWriter();
        ClawCtlConsole.WriteGatewayStarted(
            writer,
            new GatewayStartResult(
                GatewayState.Running,
                Record() with { AutostartDisabled = true },
                AlreadyRunning: false,
                Persistence: null,
                "The gateway is running."));

        string text = writer.ToString();
        Assert.Contains("explicitly", text, StringComparison.Ordinal);
        Assert.Contains("gateway-service install", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallSaysWhatItDidNotRemove()
    {
        // Removing sign-in recovery must not be confused with discarding the
        // user's session and guest data.
        using var writer = new StringWriter();
        ClawCtlConsole.WriteGatewayUninstalled(
            writer,
            new GatewayStopResult(true, "The gateway is stopped."),
            new GatewayPersistenceRemovalResult(
                true,
                true,
                "Logon recovery is removed."));

        string text = writer.ToString();
        Assert.Contains("session remove", text, StringComparison.Ordinal);
        Assert.Contains("are kept", text, StringComparison.Ordinal);
    }
}
