using System.Text.RegularExpressions;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Removes credential-shaped values from text before it enters a bundle.
/// </summary>
/// <remarks>
/// <para>
/// A bundle exists to be handed to someone else, which is the entire reason
/// this is needed. Credential <em>stores</em> are excluded outright rather than
/// redacted; this covers configuration files that legitimately belong in a
/// bundle but may carry an inline key.
/// </para>
/// <para>
/// This is deliberately described as best-effort. It matches JSON members whose
/// name looks like a secret, so a credential stored under an unexpected name,
/// or in a format other than JSON, still gets through. Promising more than that
/// would encourage sharing bundles without reading them.
/// </para>
/// </remarks>
internal static partial class DiagnosticsRedactor
{
    public const string Placeholder = "<redacted>";

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SecretMember().Replace(text, $"$1\"{Placeholder}\"");
    }

    /// <summary>
    /// Matches a JSON member whose name contains a credential word, capturing
    /// everything up to its value so only the value is replaced.
    /// </summary>
    [GeneratedRegex(
        """"("[^"]*(?:key|token|secret|password|credential)[^"]*"\s*:\s*)"(?:[^"\\]|\\.)*["]"""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretMember();
}
