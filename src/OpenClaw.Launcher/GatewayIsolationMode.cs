namespace OpenClaw.Launcher;

internal enum GatewayIsolationMode
{
    Disabled,
    Enabled
}

internal static class GatewayIsolationModeExtensions
{
    public static string ToEnvironmentValue(this GatewayIsolationMode mode) =>
        mode switch
        {
            GatewayIsolationMode.Disabled => "disabled",
            GatewayIsolationMode.Enabled => "enabled",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
}
