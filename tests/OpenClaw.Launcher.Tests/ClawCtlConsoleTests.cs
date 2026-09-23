using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
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
    public void CompletionScriptIsTheExactStandardOutput()
    {
        const string script = "# completion\nRegister-ArgumentCompleter\n";
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(
            output,
            new CompletionCommandResult(
                script,
                ProfilePath: null,
                CachePath: null,
                ExitCode: 0));

        Assert.Equal(script, output.ToString());
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
                Assert.Equal(
                    $"  Checking requirements.{Environment.NewLine}",
                    output.ToString());
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
        int spinnerSelections = 0;
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
            },
            _ =>
            {
                spinnerSelections++;
                return ClawCtlSpinner.Ascii;
            });

        Assert.Equal(42, result);
        Assert.Equal(2, spinnerSelections);
        Assert.Contains("Installing the runtime.", output.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ClawCtlConsole.NarrateWithStatusAsync(
                console,
                new ClawCtlProgress(" "),
                _ => Task.FromResult(0)));
    }

    [Fact]
    public async Task GatewayNarrationUsesKnownInteractiveProcessOutput()
    {
        using var output = new StringWriter();

        GatewayStartResult result = await ClawCtlConsole.NarrateGatewayStartAsync(
            output,
            useColor: false,
            narrate: true,
            outputIsInteractive: true,
            useUnicode: true,
            progress =>
            {
                progress.Report(new GatewayStartProgress(
                    GatewayStartStage.Launching,
                    "Launching the gateway."));
                return Task.FromResult(new GatewayStartResult(
                    GatewayState.Running,
                    new GatewayRecord(),
                    AlreadyRunning: false,
                    "The gateway is running."));
            });

        Assert.Equal(GatewayState.Running, result.State);
        Assert.Contains("Launching the gateway.", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"  {GatewayStartProgress.Initial.Message}{Environment.NewLine}",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayNarrationPreservesUnicodeCapabilityForNonConsoleOutput()
    {
        string[] unicodeFrames =
        [
            .. ClawCtlSpinner.Scuttle.Frames,
            .. ClawCtlSpinner.Bubbles.Frames,
            .. ClawCtlSpinner.TidePulse.Frames,
        ];
        using var output = new StageObservingTextWriter("Launching the gateway.");

        _ = await ClawCtlConsole.NarrateGatewayStartAsync(
            output,
            useColor: true,
            narrate: true,
            outputIsInteractive: true,
            useUnicode: true,
            async progress =>
            {
                progress.Report(new GatewayStartProgress(
                    GatewayStartStage.Launching,
                    "Launching the gateway."));

                // Every renderer writes the reported stage, and the live status
                // draws its spinner ahead of it on the same row. Waiting for the
                // stage therefore ends even when live rendering is lost, which
                // the frame assertion then reports instead of the test hanging.
                await output.StageRendered.ConfigureAwait(false);
                return new GatewayStartResult(
                    GatewayState.Running,
                    new GatewayRecord(),
                    AlreadyRunning: false,
                    "The gateway is running.");
            });

        string rendered = output.GetText();
        Assert.Contains(
            unicodeFrames,
            frame => rendered.Contains(frame, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "\U0001f980 \u2713 Gateway: running on port 18789.")]
    [InlineData(false, "[ok] Gateway: running on port 18789.")]
    public void GatewayStartOutcomeReportsVerifiedSuccess(
        bool useUnicode,
        string expected)
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteGatewayStartOutcome(
            output,
            new GatewayStartResult(
                GatewayState.Running,
                new GatewayRecord { ObservedPorts = [18789] },
                AlreadyRunning: false,
                "The gateway is running."),
            useUnicode: useUnicode);

        Assert.Equal(expected + Environment.NewLine, output.ToString());
    }

    [Theory]
    [InlineData((int)GatewayState.Starting, "still starting")]
    [InlineData((int)GatewayState.Unknown, "unverified")]
    public void UncertainGatewayOutcomeDirectsTheUserToStatus(
        int stateValue,
        string expectedState)
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteGatewayStartOutcome(
            output,
            new GatewayStartResult(
                (GatewayState)stateValue,
                new GatewayRecord(),
                AlreadyRunning: false,
                "The gateway [state] could not be verified."),
            useUnicode: true);

        string rendered = output.ToString();
        Assert.Contains(
            $"\U0001f980 ! Gateway: {expectedState}. " +
            "The gateway [state] could not be verified.",
            rendered,
            StringComparison.Ordinal);
        Assert.Contains(
            "Check with clawctl gateway-service status.",
            rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "clawctl gateway-service start",
            rendered,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StoppedGatewayOutcomeReportsFailureAndRetry()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteGatewayStartOutcome(
            output,
            new GatewayStartResult(
                GatewayState.Stopped,
                new GatewayRecord(),
                AlreadyRunning: false,
                "The gateway exited during startup."),
            useUnicode: true);

        string rendered = output.ToString();
        Assert.Contains(
            "\U0001f980 \u2717 Gateway: failed. The gateway exited during startup.",
            rendered,
            StringComparison.Ordinal);
        Assert.Contains(
            "Retry with clawctl gateway-service start.",
            rendered,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayStartExceptionReportsFailureAndRetryWithoutParsingMarkup()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteGatewayStartFailure(
            output,
            @"Gateway [launch] failed at C:\work.",
            useUnicode: false);

        string rendered = output.ToString();
        Assert.Contains(
            @"[x] Gateway: failed. Gateway [launch] failed at C:\work.",
            rendered,
            StringComparison.Ordinal);
        Assert.Contains(
            "Retry with clawctl gateway-service start.",
            rendered,
            StringComparison.Ordinal);
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

    [Theory]
    [InlineData((int)SessionAvailability.Running)]
    [InlineData((int)SessionAvailability.Stale)]
    public void StatusShowsRecordedAgentAndSharedFolder(
        int availability)
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteResult(
            output,
            new StatusCommandResult(
                new SessionStatus(
                    (SessionAvailability)availability,
                    new SessionRecord
                    {
                        SchemaVersion = SessionStateStore.CurrentSchemaVersion,
                        SandboxId = "iso:sandbox1",
                        ApplicationId = "PFN:OpenClaw.Gateway_test",
                        AgentUserName = "agent_1",
                        AgentUserSid = "S-1-5-21-0-0-0-1001",
                        WorkspacePath = @"C:\Users\agent_1\Shared",
                        Generation = "generation",
                        CreatedUtc = DateTimeOffset.UnixEpoch
                    },
                    null,
                    null),
                new GatewayStatusReport(
                    GatewayState.NotStarted,
                    null,
                    "No gateway has been started."),
                new GatewayPersistenceStatus(
                    GatewayPersistenceState.Ready,
                    GatewayPersistenceLane.TaskScheduler,
                    "Gateway recovery is configured."),
                null));

        string rendered = output.ToString();
        Assert.Contains("Agent:", rendered, StringComparison.Ordinal);
        Assert.Contains("agent_1", rendered, StringComparison.Ordinal);
        Assert.Contains("Shared folder:", rendered, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\agent_1\Shared", rendered, StringComparison.Ordinal);
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

    private sealed class StageObservingTextWriter(string stage) : StringWriter
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _stageRendered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StageRendered => _stageRendered.Task;

        public string GetText()
        {
            lock (_gate)
            {
                return base.ToString();
            }
        }

        public override void Write(char value)
        {
            lock (_gate)
            {
                base.Write(value);
                SignalIfObserved();
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                base.Write(value);
                SignalIfObserved();
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
            {
                base.Write(buffer, index, count);
                SignalIfObserved();
            }
        }

        private void SignalIfObserved()
        {
            if (GetStringBuilder().ToString().Contains(stage, StringComparison.Ordinal))
            {
                _stageRendered.TrySetResult();
            }
        }
    }

}
