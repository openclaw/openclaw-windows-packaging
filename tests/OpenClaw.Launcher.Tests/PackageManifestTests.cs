using System.Xml.Linq;

namespace OpenClaw.Launcher.Tests;

public sealed class PackageManifestTests
{
    [Fact]
    public void ManifestRequiresTheDesktopVisualCppRuntime()
    {
        XDocument manifest = LoadManifest();
        XElement dependency = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "PackageDependency");

        Assert.Equal(
            "Microsoft.VCLibs.140.00.UWPDesktop",
            (string?)dependency.Attribute("Name"));
        Assert.Equal("14.0.24217.0", (string?)dependency.Attribute("MinVersion"));
        Assert.Equal(
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
            (string?)dependency.Attribute("Publisher"));
    }

    [Fact]
    public void ManifestRegistersThePublicAndControlApplications()
    {
        XDocument manifest = LoadManifest();
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

    private static string[] Aliases(XElement application) =>
        [.. application.Descendants()
            .Where(element => element.Name.LocalName == "ExecutionAlias")
            .Select(element => (string?)element.Attribute("Alias"))
            .OfType<string>()
            .OrderBy(value => value, StringComparer.Ordinal)];

    private static XDocument LoadManifest() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Package.appxmanifest"));
}
