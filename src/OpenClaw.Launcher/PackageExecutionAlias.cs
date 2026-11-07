using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;

#if OPENCLAW_GATEWAY_RECOVERY
namespace OpenClaw.GatewayRecovery;
#else
namespace OpenClaw.Launcher;
#endif

/// <summary>
/// Resolves the package-qualified execution alias that one of this package's
/// applications declares in the installed manifest.
/// </summary>
/// <remarks>
/// Windows recreates <c>%LOCALAPPDATA%\Microsoft\WindowsApps\&lt;family&gt;\&lt;alias&gt;</c>
/// on every package update, while the install directory is versioned, so the
/// alias is the stable path a logon task or shortcut can start. The alias name
/// is read rather than assumed because a side-by-side development identity
/// renames its aliases, and must never start the base installation's.
/// </remarks>
internal static class PackageExecutionAlias
{
    public const string ManifestFileName = "AppxManifest.xml";

    public static bool TryResolve(
        string packageDirectory,
        string packageFamilyName,
        string applicationId,
        string executable,
        string localApplicationData,
        [NotNullWhen(true)] out string? aliasPath,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);

        aliasPath = null;
        string manifestPath = Path.Combine(packageDirectory, ManifestFileName);
        XDocument manifest;
        try
        {
            manifest = XDocument.Load(manifestPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or XmlException)
        {
            error = $"The package manifest '{manifestPath}' could not be read: {exception.Message}";
            return false;
        }

        XElement[] applications =
        [
            .. manifest.Descendants().Where(element =>
                element.Name.LocalName == "Application" &&
                string.Equals((string?)element.Attribute("Id"), applicationId, StringComparison.Ordinal))
        ];
        if (applications.Length != 1)
        {
            error = $"Package '{packageFamilyName}' does not declare exactly one '{applicationId}' application.";
            return false;
        }

        if (!string.Equals(
                (string?)applications[0].Attribute("Executable"),
                executable,
                StringComparison.OrdinalIgnoreCase))
        {
            error = $"Package '{packageFamilyName}' application '{applicationId}' does not target {executable}.";
            return false;
        }

        string[] aliases =
        [
            .. applications[0].Descendants()
                .Where(element =>
                    element.Name.LocalName == "Extension" &&
                    string.Equals(
                        (string?)element.Attribute("Category"),
                        "windows.appExecutionAlias",
                        StringComparison.Ordinal))
                .SelectMany(extension => extension.Descendants())
                .Where(element => element.Name.LocalName == "ExecutionAlias")
                .Select(element => (string?)element.Attribute("Alias") ?? string.Empty)
        ];
        if (aliases.Length != 1)
        {
            error = $"Package '{packageFamilyName}' application '{applicationId}' must declare exactly one execution alias.";
            return false;
        }

        // The alias becomes one path segment under the package's own alias
        // directory, so anything that could address another directory is refused.
        string alias = aliases[0];
        if (alias.Length == 0 ||
            alias.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(alias), alias, StringComparison.Ordinal) ||
            !alias.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Package '{packageFamilyName}' application '{applicationId}' declares an unexpected execution alias.";
            return false;
        }

        aliasPath = Path.Combine(
            localApplicationData,
            "Microsoft",
            "WindowsApps",
            packageFamilyName,
            alias);
        error = null;
        return true;
    }
}
