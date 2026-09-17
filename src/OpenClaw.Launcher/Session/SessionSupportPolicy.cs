using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// Decides whether this machine can host the isolated agent session that every
/// OpenClaw invocation now requires.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one execution path. A machine that cannot host a session
/// fails loudly instead of quietly relocating the user's work onto the host
/// with a different profile and different isolation.
/// </para>
/// <para>
/// Indeterminate support is not a refusal. When the Windows build cannot be
/// read and the backend probe could not run, provisioning is attempted so the
/// backend's own error surfaces rather than a guess made from missing evidence.
/// </para>
/// </remarks>
internal static class SessionSupportPolicy
{
    /// <summary>
    /// What the user can do about a machine that cannot host a session.
    /// </summary>
    public const string Remediation =
        "Isolated agent sessions require a newer version of Windows. Install " +
        "the latest Windows updates (Settings > Windows Update), or install a " +
        "newer Windows version, and run `clawctl setup` again. See the " +
        "requirements section of the OpenClaw Gateway MSIX README: " +
        "https://github.com/openclaw/openclaw-windows-packaging#requirements";

    /// <summary>
    /// Throws when this machine cannot host an isolated session.
    /// </summary>
    /// <exception cref="SessionException">
    /// The machine cannot host a session. The message carries both the reason
    /// and <see cref="Remediation"/>.
    /// </exception>
    public static void EnsureSupported(
        string? packageFamilyName,
        MxcReadinessReport readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        if (packageFamilyName is null)
        {
            throw Unsupported(
                "OpenClaw is not running from its installed package, so it has " +
                "no identity to provision an isolated session with.");
        }

        if (!readiness.RuntimeAvailable)
        {
            throw Unsupported(
                "The isolated-session runtime is unavailable: " +
                (readiness.RuntimeUnavailableReason ?? "no reason was recorded."));
        }

        if (readiness.BackendProbe is { IsolationSessionAvailable: false })
        {
            throw Unsupported(
                "This machine's isolated-session backend reported that it is " +
                "not available.");
        }

        if (readiness.BackendProbe is null &&
            readiness.HostSupport == MxcHostSupport.Unsupported)
        {
            string detail =
                "This Windows build does not support isolated agent sessions.";
            throw Unsupported(
                readiness.BackendProbeFailureReason is null
                    ? detail
                    : $"{detail} The backend probe failed: " +
                      readiness.BackendProbeFailureReason);
        }
    }

    private static SessionException Unsupported(string reason) =>
        new($"{reason} {Remediation}");
}
