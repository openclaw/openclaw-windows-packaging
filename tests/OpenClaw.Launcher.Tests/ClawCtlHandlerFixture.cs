namespace OpenClaw.Launcher.Tests;

/// <summary>
/// Builds a clawctl command tree whose operations are all inert.
/// </summary>
/// <remarks>
/// Parsing, help, and completion tests must never run a real operation. A
/// single builder keeps every such test honest about that, and means adding a
/// command does not silently leave one of them invoking production code.
/// </remarks>
internal static class ClawCtlHandlerFixture
{
    /// <summary>
    /// Every operation returns success without doing anything.
    /// </summary>
    public static ClawCtlHandlers Inert() => ForAll(_ => Task.FromResult(0));

    /// <summary>
    /// Every operation fails the test if it runs. Used where invoking any
    /// operation at all would be the defect.
    /// </summary>
    public static ClawCtlHandlers Forbidden(string because) =>
        ForAll(_ => throw new InvalidOperationException(because));

    private static ClawCtlHandlers ForAll(
        Func<CancellationToken, Task<int>> operation) =>
        new()
        {
            Setup = operation,
            SessionStatus = operation,
            SessionStop = operation,
            SessionRemove = operation,
            GatewayInstall = operation,
            GatewayStatus = operation,
            GatewayStart = operation,
            GatewayStop = operation,
            GatewayUninstall = operation,
            GatewayDiagnose = operation
        };
}
