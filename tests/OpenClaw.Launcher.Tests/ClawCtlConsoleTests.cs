using OpenClaw.Launcher.Gateway;
using Spectre.Console;
using System.Text.RegularExpressions;

namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlConsoleTests
{
    [Theory]
    [InlineData(true, "\U0001f980 clawctl status")]
    [InlineData(false, "clawctl status")]
    public void HeadingUsesCrabIdentityWhenUnicodeIsAvailable(
        bool useUnicode,
        string expected)
    {
        Assert.Equal(expected, ClawCtlConsole.FormatHeading("status", useUnicode));
    }

    [Theory]
    [InlineData(true, "\U0001f980 Hint:")]
    [InlineData(false, "Hint:")]
    public void GatewayHintUsesAvailableCrabBranding(
        bool useUnicode,
        string expectedPrefix)
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteGatewayHint(output, useUnicode: useUnicode);

        string rendered = output.ToString();
        Assert.StartsWith(expectedPrefix, rendered, StringComparison.Ordinal);
        Assert.Contains(
            "Run clawctl gateway-service start to start it.",
            rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'clawctl gateway-service start'",
            rendered,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayHintColorChangesOnlyTerminalFormatting()
    {
        using var plain = new StringWriter();
        using var colored = new StringWriter();

        ClawCtlConsole.WriteGatewayHint(plain, useUnicode: true);
        ClawCtlConsole.WriteGatewayHint(
            colored,
            useColor: true,
            useUnicode: true);

        Assert.Contains("\u001b[", colored.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            plain.ToString(),
            Regex.Replace(colored.ToString(), "\u001b\\[[0-9;]*m", string.Empty));
    }

    [Fact]
    public void WriteSetupResultShowsReadyStateAndNextAction()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(
            output,
            new SetupCommandResult(
                0,
                @"C:\package\app",
                "24.20.0",
                new GatewayPersistenceInstallResult(
                    GatewayPersistenceState.Ready,
                    GatewayPersistenceLane.TaskScheduler,
                    "ready",
                    Changed: true),
                SessionReady: true,
                RuntimeLocation: SetupRuntimeLocation.IsolatedSession));

        string summary = output.ToString();
        Assert.Contains("clawctl setup", summary, StringComparison.Ordinal);
        Assert.Contains("Package:", summary, StringComparison.Ordinal);
        Assert.Contains("Runtime:", summary, StringComparison.Ordinal);
        Assert.Contains("Recovery:", summary, StringComparison.Ordinal);
        Assert.Contains("Session:", summary, StringComparison.Ordinal);
        Assert.Contains("may want to", summary, StringComparison.Ordinal);
        Assert.Contains("Optional:", summary, StringComparison.Ordinal);
        Assert.Contains("openclaw onboard", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Run: openclaw", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteSetupWithoutIsolationReportsHostRuntime()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(
            output,
            new SetupCommandResult(
                0,
                @"C:\package\app",
                "24.20.0",
                null,
                SessionReady: false,
                RuntimeLocation: SetupRuntimeLocation.Host));

        string summary = output.ToString();
        Assert.Contains(
            "Node.js 24.20.0 installed in the host",
            summary,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "installed in the isolated session",
            summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StoppingAnAbsentGatewayDoesNotRecommendStartingIt()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(output, new GatewayCommandResult(
            "stop",
            GatewayState.NotStarted,
            "No gateway was recorded, so there was nothing to stop.",
            null,
            0));

        string summary = output.ToString();
        Assert.Contains("nothing to stop", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "clawctl gateway-service start",
            summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulTeardownPreservesStopFailureDetail()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(
            output,
            new TeardownCommandResult(new global::OpenClaw.Launcher.Session.TeardownResult(
                true,
                "OpenClaw resources were removed.",
                "Stopping the session failed before deprovisioning succeeded.",
                SessionRemoved: true)));

        string summary = output.ToString();
        Assert.Contains("Session:", summary, StringComparison.Ordinal);
        Assert.Contains("removed", summary, StringComparison.Ordinal);
        Assert.Contains(
            "stopping the session failed before deprovisioning succeeded",
            summary,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RedirectedNarrationWritesTheInitialAndReportedStages()
    {
        using var output = new StringWriter();

        int result = await ClawCtlConsole.NarrateAsync(
            output,
            useColor: false,
            narrate: true,
            new ClawCtlProgress("Checking requirements."),
            progress =>
            {
                progress.Report(new ClawCtlProgress("Checking requirements."));
                progress.Report(new ClawCtlProgress("Installing the runtime."));
                return Task.FromResult(42);
            });

        Assert.Equal(42, result);
        string rendered = output.ToString();
        Assert.DoesNotContain(
            $"Checking requirements.{Environment.NewLine}  Checking requirements.",
            rendered,
            StringComparison.Ordinal);
        Assert.Contains("Installing the runtime.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveNarrationRequiresAndRendersANonEmptyInitialStage()
    {
        using var output = new StringWriter();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(output),
        });

        int result = await ClawCtlConsole.NarrateWithStatusAsync(
            console,
            new ClawCtlProgress("Preparing the isolated session."),
            progress =>
            {
                progress.Report(new ClawCtlProgress("Installing the runtime."));
                return Task.FromResult(42);
            });

        Assert.Equal(42, result);
        Assert.Contains("Installing the runtime.", output.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ClawCtlConsole.NarrateWithStatusAsync(
                console,
                new ClawCtlProgress(" "),
                _ => Task.FromResult(0)));
    }

    [Fact]
    public void DiagnosticsBundlePathRemainsAnExactStandaloneLine()
    {
        string bundlePath = @"C:\diagnostics\" + new string('a', 120) + ".zip";
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(
            output,
            new CollectLogsCommandResult(new DiagnosticsBundleResult(
                bundlePath,
                SessionReached: true,
                Notes: [])));

        string rendered = output.ToString();
        Assert.Contains($"Bundle:{Environment.NewLine}{bundlePath}", rendered, StringComparison.Ordinal);
        Assert.Contains(bundlePath, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorChangesOnlyTerminalFormatting()
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
        using var plain = new StringWriter();
        using var colored = new StringWriter();

        ClawCtlConsole.WriteResult(plain, result);
        ClawCtlConsole.WriteResult(colored, result, useColor: true);

        Assert.Contains("\u001b[", colored.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            plain.ToString(),
            Regex.Replace(colored.ToString(), "\u001b\\[[0-9;]*m", string.Empty));
    }
}
