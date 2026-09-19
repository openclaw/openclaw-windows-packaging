using System.Text.Json;
using OpenClaw.Launcher.Gateway;
using Spectre.Console;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayAddressTests
{
    // A configured port that is among the observed listeners identifies the
    // gateway even when child processes own other listeners.
    [Fact]
    public void AConfirmedConfiguredPortWinsOverOtherObservedListeners()
    {
        var record = new GatewayRecord { Port = 18789, ObservedPorts = [3000, 18789] };

        Assert.Equal(18789, GatewayAddress.ResolvePort(record));
    }

    [Fact]
    public void AConfiguredPortIsUsedWhenNothingHasBeenObservedYet()
    {
        var record = new GatewayRecord { Port = 9100 };

        Assert.Equal(9100, GatewayAddress.ResolvePort(record));
    }

    [Fact]
    public void MultipleUnclassifiedListenersDoNotProduceAPort()
    {
        var record = new GatewayRecord { ObservedPorts = [3000, 18789] };

        Assert.Null(GatewayAddress.ResolvePort(record));
        Assert.Null(GatewayAddress.ResolvePort(null));
    }

    [Fact]
    public void ASingleObservedListenerIdentifiesThePort()
    {
        var record = new GatewayRecord { ObservedPorts = [18789] };

        Assert.Equal(18789, GatewayAddress.ResolvePort(record));
    }
}

public sealed class GatewayStartPresentationTests
{
    private static GatewayCommandResult Running() => new(
        "start",
        GatewayState.Running,
        "The gateway is running on port 18789.",
        null,
        0,
        18789,
        null);

    [Fact]
    public void ARunningGatewayReportsItsPortAndHowToOpenTheControlUi()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(output, Running());
        string text = output.ToString();

        Assert.Contains("Port:", text, StringComparison.Ordinal);
        Assert.Contains("18789", text, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", text, StringComparison.Ordinal);
        Assert.Contains("clawctl open", text, StringComparison.Ordinal);
        Assert.DoesNotContain("auth-token", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningStatusSuggestsOpeningTheControlUi()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(output, Running() with { Action = "status" });

        Assert.Contains(
            "clawctl open",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningGatewayWithoutAResolvedPortStillSuggestsOpeningTheControlUi()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(output, Running() with { Port = null });

        string text = output.ToString();
        Assert.DoesNotContain("Port:", text, StringComparison.Ordinal);
        Assert.Contains("clawctl open", text, StringComparison.Ordinal);
    }

    // A script asked for a document must not receive a browser URL or token.
    [Fact]
    public void TheJsonDocumentCarriesThePortButNoUnverifiedUrl()
    {
        using var output = new StringWriter();

        ClawCtlJson.WriteResult(output, Running());

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement gateway = document.RootElement.GetProperty("gateway");
        Assert.Equal(18789, gateway.GetProperty("port").GetInt32());
        Assert.False(gateway.TryGetProperty("url", out _));
        Assert.DoesNotContain(
            "auth-token",
            output.ToString(),
            StringComparison.Ordinal);
    }

    // A gateway that never bound has no address to report, and must not invent
    // one just to fill the row.
    [Fact]
    public void AGatewayWithoutAnAddressReportsNoUrl()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(output, new GatewayCommandResult(
            "start",
            GatewayState.Starting,
            "The gateway process started but is not listening yet.",
            null,
            1));

        string text = output.ToString();
        Assert.DoesNotContain("http://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("URL:", text, StringComparison.Ordinal);
    }

    // A start that ends in a stopped gateway is a failed start, not a tidy
    // shutdown, and must not wear the success mark that `stop` earns.
    [Fact]
    public void AGatewayThatExitedDuringStartupIsNotReportedAsASuccessfulStop()
    {
        using var started = new StringWriter();
        using var stopped = new StringWriter();

        ClawCtlConsole.WriteResult(started, new GatewayCommandResult(
            "start",
            GatewayState.Stopped,
            "The gateway exited during startup.",
            null,
            1));
        ClawCtlConsole.WriteResult(stopped, new GatewayCommandResult(
            "stop",
            GatewayState.Stopped,
            "The gateway is stopped.",
            null,
            0));

        Assert.Contains("exited during startup", started.ToString(), StringComparison.Ordinal);
        Assert.Contains("[x]", started.ToString(), StringComparison.Ordinal);
        Assert.Contains("[ok] stopped", stopped.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StoppedStatusIsNotReportedAsAStartupFailure()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(output, new GatewayCommandResult(
            "status",
            GatewayState.Stopped,
            "The gateway is stopped.",
            null,
            0));

        Assert.Contains("stopped", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "exited during startup",
            output.ToString(),
            StringComparison.Ordinal);
    }

    // Without the message a failed start reports only a state word, leaving the
    // user with nothing to act on.
    [Fact]
    public void AFailedStartExplainsItself()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(output, new GatewayCommandResult(
            "start",
            GatewayState.Starting,
            "The gateway process started but is not listening yet.",
            null,
            1));

        Assert.Contains(
            "not listening yet",
            output.ToString(),
            StringComparison.Ordinal);
    }

    // Narration is human guidance sharing stdout with the document, so when a
    // document was requested it must produce nothing at all.
    [Fact]
    public async Task NarrationIsSilentWhenItIsTurnedOff()
    {
        using var output = new StringWriter();

        int result = await ClawCtlConsole.NarrateAsync(
            output,
            useColor: false,
            narrate: false,
            GatewayStartProgress.Initial,
            progress =>
            {
                progress.Report(new GatewayStartProgress(
                    GatewayStartStage.Launching,
                    "Launching the gateway."));
                return Task.FromResult(7);
            });

        Assert.Equal(7, result);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task NarrationWritesAStageLineWhenItCannotUseASpinner()
    {
        using var output = new StringWriter();

        await ClawCtlConsole.NarrateAsync(
            output,
            useColor: false,
            narrate: true,
            GatewayStartProgress.Initial,
            progress =>
            {
                progress.Report(new GatewayStartProgress(
                    GatewayStartStage.WaitingForListener,
                    "Waiting for the gateway to start listening."));
                return Task.FromResult(0);
            });

        Assert.Contains(
            "Preparing the isolated session.",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "Waiting for the gateway to start listening.",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayNarrationStartsTheLiveStatusWithARealStage()
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
            GatewayStartProgress.Initial,
            _ => Task.FromResult(42));

        Assert.Equal(42, result);
        Assert.Contains(
            "Preparing the isolated session.",
            output.ToString(),
            StringComparison.Ordinal);
    }
}
