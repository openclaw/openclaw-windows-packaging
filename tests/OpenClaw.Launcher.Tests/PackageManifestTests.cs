using System.Xml.Linq;

namespace OpenClaw.Launcher.Tests;

public sealed class PackageManifestTests
{
    [Fact]
    public void ManifestRegistersBothAliasesToTheSingleExecutable()
    {
        XDocument manifest = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Package.appxmanifest"));
        XElement extension = Assert.Single(
            manifest.Descendants(),
            element =>
                element.Name.LocalName == "Extension" &&
                (string?)element.Attribute("Category") ==
                "windows.appExecutionAlias");
        Assert.Equal(
            "openclaw.exe",
            (string?)extension.Attribute("Executable"));

        string[] aliases = [.. extension.Descendants()
            .Where(element => element.Name.LocalName == "ExecutionAlias")
            .Select(element => (string?)element.Attribute("Alias"))
            .OfType<string>()
            .OrderBy(value => value, StringComparer.Ordinal)];
        Assert.Equal(["clawctl.exe", "openclaw.exe"], aliases);
    }
}
