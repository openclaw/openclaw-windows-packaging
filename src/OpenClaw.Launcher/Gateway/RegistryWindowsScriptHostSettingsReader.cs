using Microsoft.Win32;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Opens the Windows Script Host settings keys read-only. Nothing here writes
/// to the registry.
/// </summary>
internal sealed class RegistryWindowsScriptHostSettingsReader : IWindowsScriptHostSettingsReader
{
    public int? ReadDword(WindowsScriptHostSettingsScope scope, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        RegistryHive hive = scope == WindowsScriptHostSettingsScope.Machine
            ? RegistryHive.LocalMachine
            : RegistryHive.CurrentUser;
        try
        {
            // The native view matches the System32 wscript.exe that recovery runs.
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(
                WindowsScriptHostPolicy.SettingsKeyPath,
                writable: false);
            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or
            UnauthorizedAccessException or
            IOException)
        {
            return null;
        }
    }
}
