using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class ClawCtlGatewayCommandParsingTests
{
    [Theory]
    [InlineData("install", ClawCtlCommand.GatewayInstall)]
    [InlineData("status", ClawCtlCommand.GatewayStatus)]
    [InlineData("start", ClawCtlCommand.GatewayStart)]
    [InlineData("stop", ClawCtlCommand.GatewayStop)]
    [InlineData("uninstall", ClawCtlCommand.GatewayUninstall)]
    public void EachSubCommandIsRecognized(string verb, ClawCtlCommand expected)
    {
        ClawCtlCommandParseResult parsed =
            ClawCtlCommandParser.Parse(["gateway-service", verb]);

        Assert.Null(parsed.Error);
        Assert.Equal(expected, parsed.Command);
    }

    [Fact]
    public void TheNounIsGatewayServiceSoItCannotShadowOpenClawsOwnGatewayCommand()
    {
        // `openclaw gateway run` belongs to OpenClaw. A host `gateway` noun
        // would shadow it.
        ClawCtlCommandParseResult parsed =
            ClawCtlCommandParser.Parse(["gateway", "status"]);

        Assert.NotNull(parsed.Error);
        Assert.Equal(ClawCtlCommand.Help, parsed.Command);
    }

    [Fact]
    public void ABareNounIsAMissingSubCommandRatherThanADefault()
    {
        ClawCtlCommandParseResult parsed =
            ClawCtlCommandParser.Parse(["gateway-service"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("requires a sub-command", parsed.Error);
    }

    [Fact]
    public void AnUnknownSubCommandIsRefusedRatherThanGuessed()
    {
        ClawCtlCommandParseResult parsed =
            ClawCtlCommandParser.Parse(["gateway-service", "unistall"]);

        Assert.NotNull(parsed.Error);
        Assert.Equal(ClawCtlCommand.Help, parsed.Command);
    }

    [Fact]
    public void ExtraArgumentsAreRefused()
    {
        ClawCtlCommandParseResult parsed =
            ClawCtlCommandParser.Parse(["gateway-service", "start", "--port", "9000"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("Unexpected arguments", parsed.Error);
    }

    [Fact]
    public void HelpAndUsageNameTheGatewayCommands()
    {
        // A feature nothing mentions cannot be discovered.
        var help = new StringWriter();
        ClawCtlConsole.WriteHelp(help);
        var usage = new StringWriter();
        ClawCtlConsole.WriteUsage(usage);

        foreach (string verb in
            new[] { "install", "status", "start", "stop", "uninstall" })
        {
            Assert.Contains(
                $"gateway-service {verb}",
                help.ToString(),
                StringComparison.Ordinal);
        }

        Assert.Contains("gateway-service", usage.ToString(), StringComparison.Ordinal);
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
        var writer = new StringWriter();
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
        var writer = new StringWriter();
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
        var writer = new StringWriter();
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
