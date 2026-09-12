using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>One checked link in the chain that starts the gateway.</summary>
/// <param name="Name">What was checked.</param>
/// <param name="Ok">
/// Null when the answer is genuinely unknown, which is distinct from a failure.
/// </param>
/// <param name="Detail">The observation, not an interpretation of it.</param>
internal sealed record GatewayDiagnostic(string Name, bool? Ok, string Detail);

/// <summary>
/// Everything needed to explain why the gateway is or is not running.
/// </summary>
/// <remarks>
/// The chain is long: package identity, then a session, then a helper, then a
/// detached process, then a listener, then a logon task that can restart it
/// all. A failure anywhere reads as "the gateway isn't running", so the point
/// of this report is to say which link actually broke.
/// </remarks>
internal sealed record GatewayDiagnosticReport(
    IReadOnlyList<GatewayDiagnostic> Checks,
    GatewayStatusReport Status,
    GatewayPersistenceStatus Persistence,
    string? LogPath);

/// <summary>
/// Collects gateway diagnostics without changing anything.
/// </summary>
internal static class GatewayDiagnostics
{
    internal static async Task<GatewayDiagnosticReport> CollectAsync(
        GatewayRuntime gateway,
        HostPaths paths,
        HostOptions options,
        SessionCoordinator sessions,
        Func<CancellationToken, Task<NodeRuntime>> resolveNode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(resolveNode);

        List<GatewayDiagnostic> checks =
        [
            CheckPackageIdentity(paths),
            CheckApplication(options),
            await CheckNodeAsync(resolveNode, cancellationToken).ConfigureAwait(false),
            CheckHelper(gateway.HelperPath),
            CheckSession(sessions),
            CheckConfiguration(gateway.Configuration, paths),
            CheckLauncher(paths)
        ];

        GatewayStatusReport status = await gateway.Controller
            .GetStatusAsync(gateway.HelperPath, cancellationToken)
            .ConfigureAwait(false);
        GatewayPersistenceStatus persistence = await gateway.Persistence
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        return new GatewayDiagnosticReport(
            checks,
            status,
            persistence,
            status.Record?.LogPath);
    }

    private static GatewayDiagnostic CheckPackageIdentity(HostPaths paths) =>
        paths.PackageFamilyName is { Length: > 0 } family
            ? new GatewayDiagnostic("Package identity", true, family)
            : new GatewayDiagnostic(
                "Package identity",
                false,
                "OpenClaw is not running from its installed package, so it " +
                "cannot provision a session or register a per-package task.");

    private static GatewayDiagnostic CheckApplication(HostOptions options)
    {
        string? directory = options.PackagedApplicationDirectory;
        if (directory is null)
        {
            return new GatewayDiagnostic(
                "Packaged application",
                false,
                "The packaged OpenClaw application directory was not found.");
        }

        string entryPoint = Path.Combine(directory, "openclaw.mjs");
        return File.Exists(entryPoint)
            ? new GatewayDiagnostic("Packaged application", true, entryPoint)
            : new GatewayDiagnostic(
                "Packaged application",
                false,
                $"The entry point is missing: {entryPoint}");
    }

    private static async Task<GatewayDiagnostic> CheckNodeAsync(
        Func<CancellationToken, Task<NodeRuntime>> resolveNode,
        CancellationToken cancellationToken)
    {
        try
        {
            NodeRuntime runtime = await resolveNode(cancellationToken)
                .ConfigureAwait(false);
            return new GatewayDiagnostic(
                "Node.js",
                true,
                $"{runtime.Version} at {runtime.ExecutablePath}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new GatewayDiagnostic("Node.js", false, exception.Message);
        }
    }

    private static GatewayDiagnostic CheckHelper(string helperPath) =>
        File.Exists(helperPath)
            ? new GatewayDiagnostic("Session helper", true, helperPath)
            : new GatewayDiagnostic(
                "Session helper",
                false,
                $"The packaged session helper is missing: {helperPath}");

    private static GatewayDiagnostic CheckSession(SessionCoordinator sessions)
    {
        SessionStatus status = sessions.GetRecordedStatus();
        return status.Availability switch
        {
            SessionAvailability.Recorded => new GatewayDiagnostic(
                "Isolated session",
                true,
                $"Recorded {status.Record!.CreatedUtc:u}."),
            SessionAvailability.None => new GatewayDiagnostic(
                "Isolated session",
                null,
                "No session is recorded yet; starting the gateway creates one."),
            _ => new GatewayDiagnostic(
                "Isolated session",
                false,
                status.Detail ?? "The recorded session is unusable.")
        };
    }

    private static GatewayDiagnostic CheckConfiguration(
        GatewayConfigurationStore store,
        HostPaths paths)
    {
        try
        {
            GatewayLaunchConfiguration launch = store.Resolve(paths.StateRoot);
            return new GatewayDiagnostic(
                "Launch configuration",
                true,
                $"Port {launch.Port}, working directory {launch.WorkingDirectory}.");
        }
        catch (GatewayConfigurationException exception)
        {
            return new GatewayDiagnostic(
                "Launch configuration",
                false,
                exception.Message);
        }
    }

    private static GatewayDiagnostic CheckLauncher(HostPaths paths)
    {
        string path = paths.GatewayLauncherPath;
        if (!File.Exists(path))
        {
            return new GatewayDiagnostic(
                "Sign-in launcher",
                null,
                $"Not written yet: {path}");
        }

        try
        {
            return GatewayLauncherScript.LooksGenerated(File.ReadAllText(path))
                ? new GatewayDiagnostic("Sign-in launcher", true, path)
                : new GatewayDiagnostic(
                    "Sign-in launcher",
                    false,
                    $"'{path}' was not generated by OpenClaw.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new GatewayDiagnostic(
                "Sign-in launcher",
                null,
                $"'{path}' could not be read: {exception.Message}");
        }
    }
}
