namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlCommandTests
{
    [Fact]
    public void ParseReturnsHelpWhenNoArgumentsAreProvided()
    {
        ClawCtlCommandParseResult result = ClawCtlCommandParser.Parse([]);

        Assert.Null(result.Error);
        Assert.Equal(ClawCtlCommand.Help, result.Command);
    }

    [Fact]
    public void ParseReturnsSetupForTheSetupCommand()
    {
        ClawCtlCommandParseResult result = ClawCtlCommandParser.Parse(["setup"]);

        Assert.Null(result.Error);
        Assert.Equal(ClawCtlCommand.Setup, result.Command);
    }

    [Fact]
    public void ParseReturnsVersionForTheVersionOption()
    {
        ClawCtlCommandParseResult result =
            ClawCtlCommandParser.Parse(["--version"]);

        Assert.Null(result.Error);
        Assert.Equal(ClawCtlCommand.Version, result.Command);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("doctor")]
    [InlineData("gateway", "status")]
    [InlineData("update-package")]
    [InlineData("verify")]
    [InlineData("repair")]
    [InlineData("setup", "--force")]
    public void ParseRejectsCommandsOutsideThePublicManagementSurface(
        params string[] args)
    {
        ClawCtlCommandParseResult result = ClawCtlCommandParser.Parse(args);

        Assert.NotNull(result.Error);
    }
}
