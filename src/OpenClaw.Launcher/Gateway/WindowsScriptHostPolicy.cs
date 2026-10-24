namespace OpenClaw.Launcher.Gateway;

/// <summary>Which registry hive a Windows Script Host setting lives in.</summary>
internal enum WindowsScriptHostSettingsScope
{
    /// <summary>The machine-wide setting under <c>HKEY_LOCAL_MACHINE</c>.</summary>
    Machine,

    /// <summary>The signed-in user's setting under <c>HKEY_CURRENT_USER</c>.</summary>
    User,
}

/// <summary>
/// Reads Windows Script Host settings without changing them. The seam keeps
/// the policy decision testable without touching the host's registry.
/// </summary>
internal interface IWindowsScriptHostSettingsReader
{
    /// <summary>
    /// Reads a DWORD from the Windows Script Host <c>Settings</c> key. Returns
    /// <see langword="null"/> when the key or value is absent, is not a DWORD, or
    /// cannot be read, so an unreadable setting is never taken as evidence of a
    /// disabled host.
    /// </summary>
    int? ReadDword(WindowsScriptHostSettingsScope scope, string valueName);
}

/// <summary>
/// Decides whether Windows Script Host has been turned off, which would stop
/// both logon recovery lanes because each runs through <c>wscript.exe</c>.
/// </summary>
internal static class WindowsScriptHostPolicy
{
    public const string SettingsKeyPath =
        @"SOFTWARE\Microsoft\Windows Script Host\Settings";

    private const string EnabledValue = "Enabled";

    private const string IgnoreUserSettingsValue = "IgnoreUserSettings";

    /// <summary>
    /// Returns the registry value that disables Windows Script Host, or
    /// <see langword="null"/> when nothing does. Absence means enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Enabled</c> = 0 disables the host machine-wide under HKLM and for one
    /// user under HKCU. A machine-wide HKLM <c>IgnoreUserSettings</c> other than
    /// 0 makes the host disregard the user's HKCU settings. HKLM <c>Enabled</c>
    /// = 0 is reported even when HKCU says 1: the machine-wide value is an
    /// administrator's decision, and no per-user value is known to override it.
    /// </para>
    /// <para>
    /// Only the native registry view is read. Recovery runs
    /// <c>System32\wscript.exe</c>, so a Wow6432Node value that only a 32-bit
    /// host would consult is irrelevant.
    /// </para>
    /// </remarks>
    public static string? FindDisablingSetting(IWindowsScriptHostSettingsReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.ReadDword(WindowsScriptHostSettingsScope.Machine, EnabledValue) == 0)
        {
            return Describe(@"HKEY_LOCAL_MACHINE", EnabledValue);
        }

        bool userSettingsIgnored = reader.ReadDword(
            WindowsScriptHostSettingsScope.Machine,
            IgnoreUserSettingsValue) is not (null or 0);
        if (!userSettingsIgnored &&
            reader.ReadDword(WindowsScriptHostSettingsScope.User, EnabledValue) == 0)
        {
            return Describe(@"HKEY_CURRENT_USER", EnabledValue);
        }

        return null;
    }

    private static string Describe(string hive, string valueName) =>
        $@"{hive}\{SettingsKeyPath}\{valueName}";
}
