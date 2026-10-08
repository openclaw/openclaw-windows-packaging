using System.Text.Json;

namespace OpenClaw.Launcher.Tests.StateTransfer;

public sealed class ClawCtlStateTransferTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task BackupListingReachesProductionHandlerWithoutAReadySession()
    {
        string archive = Path.Combine(_root, "saved.tar.gz");
        await File.WriteAllBytesAsync(archive, "retained archive"u8.ToArray());
        using var output = new StringWriter();
        using var errors = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []), ["backup", "--list", "--json"],
            _ => { }, output, errors, new FailIfWorkStartsLifecycle(),
            archiveDirectory: _root);

        using JsonDocument result = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exitCode);
        Assert.Empty(errors.ToString());
        Assert.Equal(archive, result.RootElement.GetProperty("archives")[0].GetProperty("path").GetString());
        Assert.DoesNotContain("retained archive", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("backup", "--list", "--dry-run")]
    [InlineData("backup", "--list", "saved.tar.gz")]
    public async Task InvalidOptionsNeverReachTheHandler(params string[] args)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();

        int exitCode = await Program.RunControlAsync(
            new HostOptions(null, null, []), args, _ => { }, output, errors,
            new FailIfWorkStartsLifecycle(), archiveDirectory: _root);

        Assert.Equal(1, exitCode);
        Assert.Contains("--list", errors.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_root));
    }
}
