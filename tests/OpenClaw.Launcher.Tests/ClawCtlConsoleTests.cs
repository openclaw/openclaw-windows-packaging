namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlConsoleTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void WriteReadinessSummaryDescribesPackagedApplication()
    {
        using var output = new StringWriter();
        string applicationDirectory = Path.Combine(_testDirectory, "app");

        ClawCtlConsole.WriteReadinessSummary(
            output,
            applicationDirectory);

        string summary = output.ToString();
        Assert.Contains("package is ready", summary, StringComparison.Ordinal);
        Assert.Contains(applicationDirectory, summary, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
