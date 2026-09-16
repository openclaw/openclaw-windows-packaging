using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayControlOutputTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task StatusIncludesDetailAndSanitizedBoundedLogTailForAStoppedGateway()
    {
        HostPaths paths = HostPaths.ForRoot(_root, "OpenClaw.Gateway_test");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        string[] lines =
        [
            .. Enumerable.Range(1, 12).Select(index =>
                index == 12 ? "\u001b[31mfinal\u0001 line\u001b[0m" : $"line {index}")
        ];
        await File.WriteAllLinesAsync(paths.LogPath, lines).ConfigureAwait(true);
        using var output = new StringWriter();

        await GatewayControlOutput.WriteStatusAsync(
            output,
            new GatewayStatusReport(
                GatewayState.Stopped,
                null,
                "The gateway is not running.",
                "the application exited with code 78"),
            paths,
            CancellationToken.None).ConfigureAwait(true);

        string rendered = output.ToString();
        Assert.Contains("The gateway is not running.", rendered, StringComparison.Ordinal);
        Assert.Contains("the application exited with code 78", rendered, StringComparison.Ordinal);
        Assert.Contains($"Gateway log tail ({paths.LogPath}):", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain($"line 1{Environment.NewLine}", rendered, StringComparison.Ordinal);
        Assert.Contains("line 3", rendered, StringComparison.Ordinal);
        Assert.Contains("final line", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', rendered);
        Assert.DoesNotContain('\u0001', rendered);
    }

    [Fact]
    public async Task StatusKeepsFailureOutputWhenTheGuestLogIsUnavailable()
    {
        HostPaths paths = HostPaths.ForRoot(_root, "OpenClaw.Gateway_test");
        using var output = new StringWriter();

        await GatewayControlOutput.WriteStatusAsync(
            output,
            new GatewayStatusReport(
                GatewayState.Unhealthy,
                null,
                "The gateway is not serving.",
                "Inspect diagnostics before retrying."),
            paths,
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(
            $"The gateway is not serving.{Environment.NewLine}Inspect diagnostics before retrying.{Environment.NewLine}" +
            $"Gateway log unavailable: {paths.LogPath}{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public async Task StopIncludesTheUnconfirmedLaunchRecoveryInstruction()
    {
        using var output = new StringWriter();

        await GatewayControlOutput.WriteStopAsync(
            output,
            new GatewayStopResult(
                Stopped: false,
                Message: "The gateway was not stopped.",
                Detail: "Do not start a replacement. Run `clawctl teardown` to recover."))
            .ConfigureAwait(true);

        Assert.Contains("Do not start a replacement. Run `clawctl teardown` to recover.", output.ToString(), StringComparison.Ordinal);
    }
}
