using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher;

// The collaborators the host startup path needs, so that startup itself can be
// exercised outside the production process. Main supplies the real ones; the
// NativeAOT scenario driver and the xUnit suite supply fixture-owned storage
// and dependencies that cannot reach the user's profile or start a real child
// process.
internal sealed class HostStartup
{
    public required HostEntrypoint Entrypoint { get; init; }

    // Returns a log the caller owns. Production resolves the packaged
    // LocalState or %LOCALAPPDATA% location; tests pass an explicit path.
    public required Func<HostDiagnosticLog> CreateDiagnostics { get; init; }

    public required string BaseDirectory { get; init; }

    public required TextWriter Output { get; init; }

    public required TextWriter Error { get; init; }

    public Func<CancellationToken, Task<NodeRuntime>>? ResolveNode { get; init; }

    public Program.LaunchOpenClawAsync? LaunchOpenClaw { get; init; }

    // Session routing and in-session execution are seams for the same reason
    // as the launch delegate: a test host has neither a packaged identity nor
    // a real MXC backend, so production would resolve neither.
    public Func<CancellationToken, Task<SessionRoutingDecision>>? DecideRouting { get; init; }

    public Program.RunInSessionAsync? RunInSession { get; init; }

    // Control tests can substitute the gateway composition so setup exercises
    // the real lifecycle without registering a task or contacting MXC.
    public Func<HostOptions, Action<string>, GatewayRuntime>? CreateGatewayRuntime { get; init; }

    public static HostStartup CreateProduction() => new()
    {
        Entrypoint = HostEntrypointResolver.Resolve(),
        CreateDiagnostics = HostDiagnosticLog.Create,
        BaseDirectory = AppContext.BaseDirectory,
        Output = Console.Out,
        Error = Console.Error
    };
}
