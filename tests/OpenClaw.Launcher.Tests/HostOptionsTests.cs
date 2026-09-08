namespace OpenClaw.Launcher.Tests;

public sealed class HostOptionsTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void ParseForwardsAllArgumentsUnchanged()
    {
        HostOptions options = HostOptions.Parse(
        [
            "--host-payload", "payload.tar.gz",
            "--host-node", "test-node.exe",
            "--",
            "gateway", "run", "--port", "12345"
        ]);

        Assert.Equal(
            [
                "--host-payload", "payload.tar.gz",
                "--host-node", "test-node.exe",
                "--",
                "gateway", "run", "--port", "12345"
            ],
            options.OpenClawArguments);
    }

    [Fact]
    public void ParseReportsMissingPackagedApplication()
    {
        HostOptions options = HostOptions.Parse([], _testDirectory);

        Assert.Null(options.PackagedApplicationDirectory);
        Assert.Empty(options.OpenClawArguments);
    }

    [Fact]
    public void ParseResolvesPackagedApplicationWhenEntryPointExists()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");

        HostOptions options = HostOptions.Parse(
            ["gateway", "run"],
            _testDirectory);

        Assert.Equal(
            applicationDirectory,
            options.PackagedApplicationDirectory);
        Assert.Equal(["gateway", "run"], options.OpenClawArguments);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
