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

    public Session.IInstallationLifecycle? InstallationLifecycle { get; init; }

    public Func<string, string?>? ReadEnvironmentVariable { get; init; }

    public static HostStartup CreateProduction() => new()
    {
        Entrypoint = HostEntrypointResolver.Resolve(),
        CreateDiagnostics = HostDiagnosticLog.Create,
        BaseDirectory = AppContext.BaseDirectory,
        Output = Console.Out,
        Error = Console.Error,
        InstallationLifecycle = Session.InstallationLifecycle.Production
    };
}
