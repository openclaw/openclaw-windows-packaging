using System.Text.RegularExpressions;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Removes credential-shaped values from text before it enters a bundle.
/// </summary>
/// <remarks>
/// <para>
/// A bundle exists to be handed to someone else, which is the entire reason
/// this is needed. Credential <em>stores</em> are excluded outright rather than
/// redacted; this covers logs and configuration files that legitimately belong
/// in a bundle but may carry an inline key.
/// </para>
/// <para>
/// This is deliberately described as best-effort. It matches JSON members, URL
/// query or fragment parameters, and environment-style assignments whose name
/// marks a secret, and credentials in an HTTP authorization header. A
/// credential stored under an unexpected name or in another form still gets
/// through. Promising more than that would encourage sharing bundles without
/// reading them.
/// </para>
/// <para>
/// Every pattern runs non-backtracking. Much of the redacted text is written
/// by the guest, and a backtracking engine lets crafted input take quadratic
/// time over a megabyte-sized log, which would stall collection.
/// </para>
/// </remarks>
internal static partial class DiagnosticsRedactor
{
    public const string Placeholder = "<redacted>";

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string redacted = SecretMember().Replace(text, $"$1\"{Placeholder}\"");
        redacted = SecretParameter().Replace(redacted, $"$1{Placeholder}");
        redacted = SecretAssignment().Replace(redacted, $"$1{Placeholder}");
        return AuthorizationHeader().Replace(redacted, $"$1{Placeholder}");
    }

    /// <summary>
    /// Matches a JSON member whose name contains a credential word, capturing
    /// everything up to its value so only the value is replaced.
    /// </summary>
    [GeneratedRegex(
        """"("[^"]*(?:key|token|secret|password|credential)[^"]*"\s*:\s*)"(?:[^"\\]|\\.)*["]"""",
        Options)]
    private static partial Regex SecretMember();

    /// <summary>
    /// Matches a URL query or fragment parameter whose name contains a
    /// credential word, such as a Control UI link's <c>#token=</c>, capturing
    /// the name so only the value is replaced.
    /// </summary>
    [GeneratedRegex(
        """([?#&][^=&#\s"'<>]*(?:key|token|secret|password|credential)[^=&#\s"'<>]*=)[^&#\s"'<>]+""",
        Options)]
    private static partial Regex SecretParameter();

    /// <summary>
    /// Matches an environment-style assignment, such as
    /// <c>OPENAI_API_KEY=...</c>, whose upper-case name contains a credential
    /// word. Case matters here, so ordinary prose containing "key=" survives.
    /// </summary>
    [GeneratedRegex(
        """\b([A-Z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL)[A-Z0-9_]*=)[^\s"']+""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretAssignment();

    /// <summary>
    /// Matches the credential in an HTTP authorization header, in header or
    /// JSON form, capturing the scheme so only the credential is replaced.
    /// Requiring the header name keeps prose such as "Basic authentication" intact.
    /// </summary>
    [GeneratedRegex(
        """(\bAuthorization["']?\s*[:=]\s*["']?(?:Bearer|Basic|Token|Digest)\s+)[^\s"',;]+""",
        Options)]
    private static partial Regex AuthorizationHeader();
}
