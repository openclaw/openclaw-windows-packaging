namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlColorPolicyTests
{
    [Fact]
    public void InteractiveConsoleUsesColorByDefault()
    {
        bool enabled = ClawCtlColorPolicy.ShouldUseColor(
            noColor: false,
            json: false,
            outputIsConsole: true,
            consoleIsInteractive: true,
            _ => null);

        Assert.True(enabled);
    }

    [Theory]
    [InlineData(true, false, null, null)]
    [InlineData(false, true, null, null)]
    [InlineData(false, false, "1", null)]
    [InlineData(false, false, null, "true")]
    public void ExplicitAndNonInteractiveConditionsDisableColor(
        bool noColor,
        bool json,
        string? noColorEnvironment,
        string? continuousIntegration)
    {
        string? ReadEnvironment(string name) => name switch
        {
            "NO_COLOR" => noColorEnvironment,
            "CI" => continuousIntegration,
            _ => null
        };

        bool enabled = ClawCtlColorPolicy.ShouldUseColor(
            noColor,
            json,
            outputIsConsole: true,
            consoleIsInteractive: true,
            ReadEnvironment);

        Assert.False(enabled);
    }

    [Fact]
    public void ForceColorEnablesRedirectedOutput()
    {
        static string? ReadEnvironment(string name) =>
            name == "FORCE_COLOR" ? "1" : null;

        bool enabled = ClawCtlColorPolicy.ShouldUseColor(
            noColor: false,
            json: false,
            outputIsConsole: false,
            consoleIsInteractive: false,
            ReadEnvironment);

        Assert.True(enabled);
    }

    [Fact]
    public void ForceColorDoesNotRequireVirtualTerminalSetupForRedirectedOutput()
    {
        bool enableCalled = false;
        static string? ReadEnvironment(string name) =>
            name == "FORCE_COLOR" ? "1" : null;

        bool enabled = ClawCtlColorPolicy.PrepareOutput(
            noColor: false,
            json: false,
            outputIsProcessConsoleWriter: true,
            consoleIsInteractive: false,
            ReadEnvironment,
            () =>
            {
                enableCalled = true;
                return false;
            });

        Assert.True(enabled);
        Assert.False(enableCalled);
    }

    [Fact]
    public void InteractiveColorRequiresVirtualTerminalSetup()
    {
        bool enableCalled = false;

        bool enabled = ClawCtlColorPolicy.PrepareOutput(
            noColor: false,
            json: false,
            outputIsProcessConsoleWriter: true,
            consoleIsInteractive: true,
            _ => null,
            () =>
            {
                enableCalled = true;
                return false;
            });

        Assert.False(enabled);
        Assert.True(enableCalled);
    }
}
