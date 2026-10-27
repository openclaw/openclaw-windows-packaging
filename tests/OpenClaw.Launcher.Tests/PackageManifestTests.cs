using System.Xml.Linq;

namespace OpenClaw.Launcher.Tests;

public sealed class PackageManifestTests
{
    [Fact]
    public void ManifestRegistersThePublicAndControlApplications()
    {
        XDocument manifest = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Package.appxmanifest"));
        XElement publicApplication = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "Application" &&
                (string?)element.Attribute("Id") == "App");
        XElement controlApplication = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "Application" &&
                (string?)element.Attribute("Id") == "Control");
        Assert.Equal(
            "openclaw.exe",
            (string?)publicApplication.Attribute("Executable"));
        Assert.Equal(
            "openclaw.exe",
            (string?)controlApplication.Attribute("Executable"));

        Assert.Equal(
            ["openclaw.exe"],
            Aliases(publicApplication));
        Assert.Equal(
            ["clawctl.exe"],
            Aliases(controlApplication));
    }

    [Fact]
    public void ManifestRegistersTheWindowlessSignInHelperWithItsOwnAlias()
    {
        XDocument manifest = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Package.appxmanifest"));
        XElement helper = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "Application" &&
                (string?)element.Attribute("Id") == Launcher.Gateway.GatewayPersistenceManager.HelperApplicationId);

        Assert.Equal(
            Launcher.Gateway.GatewayPersistenceManager.HelperExecutable,
            (string?)helper.Attribute("Executable"));
        Assert.Equal(
            [Launcher.Gateway.GatewayPersistenceManager.HelperExecutable],
            Aliases(helper));
        Assert.Equal(
            "none",
            (string?)helper.Descendants()
                .Single(element => element.Name.LocalName == "VisualElements")
                .Attribute("AppListEntry"));
    }

    private static string[] Aliases(XElement application) =>
        [.. application.Descendants()
            .Where(element => element.Name.LocalName == "ExecutionAlias")
            .Select(element => (string?)element.Attribute("Alias"))
            .OfType<string>()
            .OrderBy(value => value, StringComparer.Ordinal)];
}
