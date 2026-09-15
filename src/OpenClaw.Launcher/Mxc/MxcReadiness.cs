using System.Diagnostics.CodeAnalysis;

namespace OpenClaw.Launcher.Mxc;

/// <summary>
/// Windows build identity, including the update build revision that the
/// documented IsolationSession minimum specifies.
/// </summary>
internal sealed record MxcHostBuild(int Build, int UpdateBuildRevision)
{
    public override string ToString() => $"{Build}.{UpdateBuildRevision}";
}

internal enum MxcHostSupport
{
    /// <summary>The host build could not be determined.</summary>
    Unknown,

    Supported,

    Unsupported
}

/// <summary>
/// How <see cref="MxcReadinessReport.HostSupport"/> was established. The
/// distinction matters: the documented minimum build predicts support, while
/// the backend probe measures it, and only the probe notices a host where the
/// feature is present but unusable.
/// </summary>
internal enum MxcSupportEvidence
{
    /// <summary>Nothing could be established.</summary>
    None,

    /// <summary>Inferred from the Windows build against the documented minimum.</summary>
    HostBuild,

    /// <summary>Measured by the runtime's own host capability detector.</summary>
    BackendProbe
}

internal sealed record MxcReadinessReport(
    string? RuntimeDirectory,
    MxcRuntimeProvenance? Provenance,
    string? RuntimeUnavailableReason,
    MxcHostSupport HostSupport,
    MxcHostBuild? HostBuild,
    MxcSupportEvidence SupportEvidence,
    MxcBackendProbe? BackendProbe = null,
    string? BackendProbeFailureReason = null)
{
    public bool RuntimeAvailable => RuntimeUnavailableReason is null;
}

/// <summary>
/// Read-only MXC prerequisite probe. It inspects the staged runtime, asks the
/// runtime's own capability detector about the host, and falls back to the
/// documented minimum build when that detector cannot run. It never provisions,
/// starts, or otherwise mutates state.
/// </summary>
internal static class MxcReadiness
{
    /// <summary>
    /// Minimum Windows build documented by the pinned runtime for the
    /// IsolationSession backend. Used only when the backend probe is
    /// unavailable, because a build number predicts support rather than
    /// measuring it.
    /// </summary>
    public static readonly MxcHostBuild MinimumHostBuild = new(26340, 9212);

    /// <summary>
    /// A report for a caller that has already ruled the runtime out and must
    /// not pay for, or fail on, a probe it will ignore.
    /// </summary>
    public static MxcReadinessReport Unavailable(string reason) =>
        new(
            null,
            null,
            reason,
            MxcHostSupport.Unknown,
            null,
            MxcSupportEvidence.None);

    public static Task<MxcReadinessReport> ProbeAsync(
        CancellationToken cancellationToken) =>
        ProbeAsync(
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable,
            WindowsHostBuild.TryRead,
            static (location, token) =>
                new MxcCliSessionClient(location).ProbeBackendAsync(token),
            cancellationToken);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "Readiness detection reports what it could establish; it never " +
            "decides an operation. A host detector that fails for an " +
            "unanticipated reason is a gap in the evidence, so the reason is " +
            "recorded and the report degrades to the build check. Narrowing " +
            "this would instead make `clawctl setup` fail on a machine it was " +
            "only being asked to describe.")]
    internal static async Task<MxcReadinessReport> ProbeAsync(
        string baseDirectory,
        Func<string, string?> readEnvironmentVariable,
        Func<MxcHostBuild?> readHostBuild,
        Func<MxcRuntimeLocation, CancellationToken, Task<MxcBackendProbe>> probeBackend,
        CancellationToken cancellationToken)
    {
        string? runtimeDirectory = null;
        MxcRuntimeProvenance? provenance = null;
        string? unavailableReason = null;
        MxcRuntimeLocation? location = null;

        try
        {
            location = MxcRuntimeLocator.Locate(
                baseDirectory,
                readEnvironmentVariable);
            runtimeDirectory = location.Directory;
            provenance = location.Provenance;
        }
        catch (MxcException exception)
        {
            unavailableReason = exception.Message;
        }

        MxcHostBuild? hostBuild = readHostBuild();
        MxcBackendProbe? backendProbe = null;
        string? probeFailure = null;

        if (location is not null)
        {
            try
            {
                backendProbe = await probeBackend(location, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MxcException exception)
            {
                probeFailure = exception.Message;
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            // A host detector that cannot run is a readiness gap, not a reason
            // to fail setup, so any other launch failure degrades to the build
            // check rather than propagating.
            catch (Exception exception)
            {
                probeFailure = exception.Message;
            }
        }

        (MxcHostSupport support, MxcSupportEvidence evidence) =
            backendProbe is not null
                ? (backendProbe.IsolationSessionAvailable
                    ? MxcHostSupport.Supported
                    : MxcHostSupport.Unsupported,
                   MxcSupportEvidence.BackendProbe)
                : (Classify(hostBuild),
                   hostBuild is null
                       ? MxcSupportEvidence.None
                       : MxcSupportEvidence.HostBuild);

        return new MxcReadinessReport(
            runtimeDirectory,
            provenance,
            unavailableReason,
            support,
            hostBuild,
            evidence,
            backendProbe,
            probeFailure);
    }

    private static MxcHostSupport Classify(MxcHostBuild? hostBuild)
    {
        if (hostBuild is null)
        {
            return MxcHostSupport.Unknown;
        }

        if (hostBuild.Build != MinimumHostBuild.Build)
        {
            return hostBuild.Build > MinimumHostBuild.Build
                ? MxcHostSupport.Supported
                : MxcHostSupport.Unsupported;
        }

        return hostBuild.UpdateBuildRevision >= MinimumHostBuild.UpdateBuildRevision
            ? MxcHostSupport.Supported
            : MxcHostSupport.Unsupported;
    }
}

internal static class WindowsHostBuild
{
    private const string CurrentVersionKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>
    /// Reads the running Windows build and update build revision. The revision
    /// is only available from the registry, so an unreadable value yields an
    /// unknown build rather than a fabricated revision of zero, which would
    /// wrongly report a serviced host as unsupported.
    /// </summary>
    public static MxcHostBuild? TryRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        int build = Environment.OSVersion.Version.Build;
        if (build <= 0)
        {
            return null;
        }

        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            return key?.GetValue("UBR") is int revision
                ? new MxcHostBuild(build, revision)
                : null;
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
