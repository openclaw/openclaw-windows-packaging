namespace OpenClaw.Launcher.Tests;

public sealed class PackageExecutionAliasTests : IDisposable
{
    private const string PackageFamilyName = "OpenClaw.Gateway_test";

    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LocalAppData => Path.Combine(_root, "local");

    private string AliasDirectory =>
        Path.Combine(LocalAppData, "Microsoft", "WindowsApps", PackageFamilyName);

    [Theory]
    [InlineData("GatewayRecovery", "openclaw-gateway-recovery.exe", "openclaw-gateway-recovery.exe")]
    [InlineData("Control", "openclaw.exe", "clawctl.exe")]
    public void ThePackagedManifestResolvesThePackageQualifiedAlias(
        string applicationId,
        string executable,
        string alias)
    {
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Package.appxmanifest"),
            Path.Combine(_root, PackageExecutionAlias.ManifestFileName));

        bool resolved = PackageExecutionAlias.TryResolve(
            _root, PackageFamilyName, applicationId, executable, LocalAppData,
            out string? path, out string? error);

        Assert.True(resolved, error);
        Assert.Equal(Path.Combine(AliasDirectory, alias), path);
    }

    [Fact]
    public void ASideBySideIdentityResolvesItsOwnRenamedAlias()
    {
        WriteManifest("Control", "openclaw.exe", "<ExecutionAlias Alias=\"clawctl-foo.exe\" />");

        Assert.True(PackageExecutionAlias.TryResolve(
            _root, PackageFamilyName, "Control", "openclaw.exe", LocalAppData,
            out string? path, out _));
        Assert.Equal(Path.Combine(AliasDirectory, "clawctl-foo.exe"), path);
    }

    [Theory]
    [InlineData("Other", "openclaw.exe", "<ExecutionAlias Alias=\"clawctl.exe\" />", "exactly one 'Control' application")]
    [InlineData("Control", "other.exe", "<ExecutionAlias Alias=\"clawctl.exe\" />", "does not target openclaw.exe")]
    [InlineData("Control", "openclaw.exe", "", "exactly one execution alias")]
    [InlineData("Control", "openclaw.exe", "<ExecutionAlias Alias=\"a.exe\" /><ExecutionAlias Alias=\"b.exe\" />", "exactly one execution alias")]
    [InlineData("Control", "openclaw.exe", "<ExecutionAlias Alias=\"..\\clawctl.exe\" />", "unexpected execution alias")]
    [InlineData("Control", "openclaw.exe", "<ExecutionAlias Alias=\"sub\\clawctl.exe\" />", "unexpected execution alias")]
    [InlineData("Control", "openclaw.exe", "<ExecutionAlias Alias=\"clawctl.cmd\" />", "unexpected execution alias")]
    public void AManifestThatCannotNameOneSafeAliasIsRefused(
        string applicationId,
        string executable,
        string aliases,
        string expectedError)
    {
        WriteManifest(applicationId, executable, aliases);

        bool resolved = PackageExecutionAlias.TryResolve(
            _root, PackageFamilyName, "Control", "openclaw.exe", LocalAppData,
            out string? path, out string? error);

        Assert.False(resolved);
        Assert.Null(path);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableManifestIsReportedRatherThanThrown()
    {
        bool resolved = PackageExecutionAlias.TryResolve(
            _root, PackageFamilyName, "Control", "openclaw.exe", LocalAppData,
            out _, out string? error);

        Assert.False(resolved);
        Assert.Contains(PackageExecutionAlias.ManifestFileName, error, StringComparison.Ordinal);
    }

    private void WriteManifest(string applicationId, string executable, string aliases) =>
        File.WriteAllText(
            Path.Combine(_root, PackageExecutionAlias.ManifestFileName),
            $"""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
              <Applications>
                <Application Id="{applicationId}" Executable="{executable}">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>{aliases.Replace("<ExecutionAlias", "<uap5:ExecutionAlias", StringComparison.Ordinal)}</uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
}
