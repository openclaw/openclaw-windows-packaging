using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// Windows Script Host settings held in memory. Tests must never read or
/// change the host's registry to decide whether the script host is enabled.
/// </summary>
internal sealed class FakeWindowsScriptHostSettings : IWindowsScriptHostSettingsReader
{
    private readonly Dictionary<(WindowsScriptHostSettingsScope, string), int?> _values = [];

    public void Set(WindowsScriptHostSettingsScope scope, string name, int? value) =>
        _values[(scope, name)] = value;

    public int? ReadDword(WindowsScriptHostSettingsScope scope, string valueName) =>
        _values.TryGetValue((scope, valueName), out int? value) ? value : null;
}
