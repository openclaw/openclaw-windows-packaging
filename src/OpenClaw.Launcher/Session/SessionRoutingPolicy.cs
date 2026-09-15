using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// Whether the user has asked for, or ruled out, isolated-session execution.
/// </summary>
internal enum SessionMode
{
    /// <summary>Use a session wherever the backend reports support.</summary>
    Automatic,

    /// <summary>Never use a session.</summary>
    Disabled,

    /// <summary>Use a session, and fail rather than run outside one.</summary>
    Required,
}

/// <summary>
/// Where an <c>openclaw</c> invocation will run.
/// </summary>
internal enum SessionRouting
{
    /// <summary>Inside the owned isolated session.</summary>
    Session,

    /// <summary>Directly on the host, as before this feature existed.</summary>
    Direct,
}

/// <summary>
/// The routing choice and the reason for it, so diagnostics can explain it.
/// </summary>
internal sealed record SessionRoutingDecision(SessionRouting Routing, string Reason);

/// <summary>
/// Chooses between isolated-session and direct execution.
/// </summary>
internal static class SessionRoutingPolicy
{
    /// <summary>
    /// Overrides the automatic choice. Unset means automatic.
    /// </summary>
    public const string ModeVariable = "OPENCLAW_SESSION";

    public static SessionMode ReadMode(Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        string? value = readEnvironmentVariable(ModeVariable)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return SessionMode.Automatic;
        }

        // Upper-case normalization, because CA1308 warns that lower-casing can
        // lose information for some cultures. The comparison set is ASCII, so
        // either direction matches; upper-case is the safe convention.
        return value.ToUpperInvariant() switch
        {
            "0" or "FALSE" or "OFF" or "NO" => SessionMode.Disabled,
            "1" or "TRUE" or "ON" or "YES" => SessionMode.Required,

            // An unrecognized value is not treated as "off". Silently ignoring
            // it would run outside the session the user was trying to request.
            _ => throw new SessionException(
                $"{ModeVariable} is set to '{value}', which is not one of " +
                "1, 0, true, false, on, off, yes, or no."),
        };
    }

    /// <summary>
    /// Decides where to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A machine that cannot host sessions runs directly, exactly as it did
    /// before this feature existed. That is a capability of the machine, not a
    /// failure to hide.
    /// </para>
    /// <para>
    /// There is deliberately no fallback once a session is chosen. A backend
    /// that breaks on a supported machine must surface, not quietly relocate
    /// the user's work onto the host with a different profile and different
    /// isolation.
    /// </para>
    /// </remarks>
    public static SessionRoutingDecision Decide(
        SessionMode mode,
        string? packageFamilyName,
        MxcReadinessReport readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        if (mode == SessionMode.Disabled)
        {
            return new SessionRoutingDecision(
                SessionRouting.Direct,
                $"{ModeVariable} is set to 0.");
        }

        if (packageFamilyName is null)
        {
            return Unavailable(
                mode,
                "OpenClaw is not running from its installed package, so it has " +
                "no identity to provision an isolated session with.");
        }

        if (!readiness.RuntimeAvailable)
        {
            return Unavailable(
                mode,
                "The isolated-session runtime is unavailable: " +
                (readiness.RuntimeUnavailableReason ?? "no reason was reported."));
        }

        if (readiness.BackendProbe is { IsolationSessionAvailable: false })
        {
            return Unavailable(
                mode,
                "This machine's isolated-session backend reported that it is " +
                "not available.");
        }

        if (readiness.BackendProbe is null &&
            readiness.HostSupport != MxcHostSupport.Supported)
        {
            string detail = readiness.HostSupport == MxcHostSupport.Unsupported
                ? "This Windows build does not support isolated agent sessions."
                : "Isolated-session support could not be determined on this machine.";
            return Unavailable(
                mode,
                readiness.BackendProbeFailureReason is null
                    ? detail
                    : $"{detail} The backend probe failed: " +
                      readiness.BackendProbeFailureReason);
        }

        return new SessionRoutingDecision(
            SessionRouting.Session,
            "The isolated-session backend is available.");
    }

    private static SessionRoutingDecision Unavailable(SessionMode mode, string reason) =>
        mode == SessionMode.Required
            ? throw new SessionException(
                $"{ModeVariable} requires an isolated session, but one cannot " +
                $"be used. {reason}")
            : new SessionRoutingDecision(SessionRouting.Direct, reason);
}
