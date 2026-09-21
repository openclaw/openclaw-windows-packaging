using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class ControlUiHandoffTests
{
    [Fact]
    public void AcceptsAbsoluteLoopbackHttpUrlWithoutRewritingIt()
    {
        const string url = "http://127.0.0.1:18789/control/#token=secret";

        bool parsed = ControlUiHandoffParser.TryParse(
            $$"""{"ok":true,"browserUrl":"{{url}}"}""",
            [18789],
            out string? browserUrl);

        Assert.True(parsed);
        Assert.NotNull(browserUrl);
        Assert.Equal(url, browserUrl);
    }

    [Theory]
    [InlineData("""{"ok":false,"browserUrl":"http://127.0.0.1:18789/"}""")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"browserUrl":"https://example.com/"}""")]
    [InlineData("""{"ok":true,"browserUrl":"file:///C:/temp"}""")]
    [InlineData("not-json")]
    public void RejectsUnsafeOrInvalidHandoffs(string output)
    {
        bool parsed = ControlUiHandoffParser.TryParse(
            output,
            [18789],
            out string? browserUrl);

        Assert.False(parsed);
        Assert.Null(browserUrl);
    }
    [Fact]
    public void RejectsLoopbackUrlOnAnUnobservedPort()
    {
        bool parsed = ControlUiHandoffParser.TryParse(
            """{"ok":true,"browserUrl":"http://127.0.0.1:3000/#token=secret"}""",
            [18789],
            out string? browserUrl);

        Assert.False(parsed);
        Assert.Null(browserUrl);
    }

    [Fact]
    public void RejectsHandoffWhenNoGatewayPortWasObserved()
    {
        bool parsed = ControlUiHandoffParser.TryParse(
            """{"ok":true,"browserUrl":"http://127.0.0.1:18789/#token=secret"}""",
            [],
            out string? browserUrl);

        Assert.False(parsed);
        Assert.Null(browserUrl);
    }

}
