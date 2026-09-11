namespace OpenClaw.Launcher.Mxc;

/// <summary>
/// Windows build identity, including the update build revision that the
/// documented IsolationSession minimum specifies.
/// </summary>
public sealed record MxcHostBuild(int Build, int UpdateBuildRevision)
{
    public override string ToString() => $"{Build}.{UpdateBuildRevision}";
}

public enum MxcHostSupport
{
    /// <summary>The host build could not be determined.</summary>
    Unknown,

    Supported,

    Unsupported
}

public sealed record MxcReadinessReport(
    string? RuntimeDirectory,
    MxcRuntimeProvenance? Provenance,
    string? RuntimeUnavailableReason,
    MxcHostSupport HostSupport,
    MxcHostBuild? HostBuild)
{
    public bool RuntimeAvailable => RuntimeUnavailableReason is null;
}

/// <summary>
/// Read-only MXC prerequisite probe. It inspects the staged runtime and the
/// host build only; it never provisions, starts, or otherwise mutates state.
/// </summary>
public static class MxcReadiness
{
    /// <summary>
    /// Minimum Windows build documented by the pinned runtime for the
    /// IsolationSession backend.
    /// </summary>
    public static readonly MxcHostBuild MinimumHostBuild = new(26340, 9212);

    public static MxcReadinessReport Probe() =>
        Probe(
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable,
            WindowsHostBuild.TryRead);

    internal static MxcReadinessReport Probe(
        string baseDirectory,
        Func<string, string?> readEnvironmentVariable,
        Func<MxcHostBuild?> readHostBuild)
    {
        string? runtimeDirectory = null;
        MxcRuntimeProvenance? provenance = null;
        string? unavailableReason = null;

        try
        {
            MxcRuntimeLocation location = MxcRuntimeLocator.Locate(
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
        return new MxcReadinessReport(
            runtimeDirectory,
            provenance,
            unavailableReason,
            Classify(hostBuild),
            hostBuild);
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
