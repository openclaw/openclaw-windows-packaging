namespace OpenClaw.Launcher.Gateway;

internal sealed record GatewayLaunchReceipt(
    string AttemptId,
    string? Disposition = null,
    string? StopOutcome = null,
    string? Refusal = null)
{
    public int Version { get; init; } = 1;
}

internal static class GatewayLaunchAttempt
{
    public static bool IsValid(string value) =>
        Guid.TryParseExact(value, "N", out Guid id) &&
        id != Guid.Empty &&
        string.Equals(value, id.ToString("N"), StringComparison.Ordinal);
}
