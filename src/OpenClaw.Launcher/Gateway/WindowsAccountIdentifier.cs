using System.Security.Principal;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Compares two ways of naming the same Windows account.
/// </summary>
/// <remarks>
/// Task Scheduler does not store back what it was given: registering a logon
/// trigger whose <c>UserId</c> is a SID returns a trigger whose <c>UserId</c>
/// is the account name, while the principal keeps its SID. Comparing the two
/// as text therefore reports drift on every probe, which would make status
/// permanently wrong and make install re-register forever.
/// </remarks>
internal static class WindowsAccountIdentifier
{
    /// <summary>
    /// Maps an account name or SID string to its SID, or null when it cannot
    /// be resolved. A failed lookup is not a match and not an error: the caller
    /// falls back to comparing the values as written.
    /// </summary>
    public static string? ToSid(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return new SecurityIdentifier(value).Value;
        }
        catch (ArgumentException)
        {
            // Not already a SID, so try it as an account name.
        }

        try
        {
            return ((SecurityIdentifier)new NTAccount(value)
                .Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception exception) when (
            exception is IdentityNotMappedException or SystemException)
        {
            // An unmapped or unreachable account is reported as unknown rather
            // than guessed in either direction.
            return null;
        }
    }

    /// <summary>
    /// True when both values name the same account, whether each is written as
    /// a SID or as an account name.
    /// </summary>
    public static bool SameAccount(
        string left,
        string right,
        Func<string, string?>? toSid = null)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        toSid ??= ToSid;
        return toSid(left) is { } leftSid &&
               toSid(right) is { } rightSid &&
               string.Equals(leftSid, rightSid, StringComparison.OrdinalIgnoreCase);
    }
}
