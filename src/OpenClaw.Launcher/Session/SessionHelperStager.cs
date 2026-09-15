using System.Security.Cryptography;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// Stages the packaged guest helper into the session's shared workspace.
/// </summary>
/// <remarks>
/// IsolationSession agent identities cannot execute binaries directly from
/// another package's WindowsApps directory. The shared workspace is visible
/// to both identities, so setup copies the immutable packaged helper there
/// before any guest command is dispatched.
/// </remarks>
internal static class SessionHelperStager
{
    private const string StagingDirectoryName = ".openclaw-session-host";

    public static string Stage(string packagedHelperPath, string workspacePath)
    {
        string source = RequirePackagedHelper(packagedHelperPath);
        var stagingRecord = new SessionRecord
        {
            SandboxId = "iso:helper-staging",
            ApplicationId = "helper-staging",
            WorkspacePath = workspacePath,
            Generation = typeof(SessionHelperStager).Assembly
                .GetName()
                .Version?
                .ToString() ?? "unknown"
        };
        using var operation = new SessionWorkspaceOperation(stagingRecord, _ => true);
        string destination = ResolveStagedPath(operation.WorkspacePath);
        var sourceInfo = new FileInfo(source);

        if (File.Exists(destination) &&
            FilesMatch(source, destination, sourceInfo.Length))
        {
            return destination;
        }

        string? destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new SessionException(
                $"The staged helper path has no parent directory: {destination}");
        }

        operation.EnsureDirectory(destinationDirectory);
        string temporaryPath = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporaryPath, overwrite: false);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new SessionException(
                "The packaged session helper could not be staged into the " +
                $"shared workspace: {exception.Message}",
                exception);
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        return destination;
    }

    public static string RequireStaged(
        string packagedHelperPath,
        string workspacePath)
    {
        _ = RequirePackagedHelper(packagedHelperPath);
        string path = ResolveStagedPath(workspacePath);
        return File.Exists(path)
            ? path
            : throw new SessionException(
                "The isolated-session helper has not been staged for this " +
                "package version. Run `clawctl setup` again.");
    }

    internal static string ResolveStagedPath(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        string version = typeof(SessionHelperStager).Assembly
            .GetName()
            .Version?
            .ToString() ?? "unknown";
        return Path.GetFullPath(
            Path.Combine(
                workspacePath,
                StagingDirectoryName,
                version,
                SessionRuntime.HelperFileName));
    }

    internal static string RequirePackagedHelper(string packagedHelperPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagedHelperPath);

        string path = Path.GetFullPath(packagedHelperPath);
        return File.Exists(path)
            ? path
            : throw new SessionException(
                $"The packaged session helper is missing: {path}");
    }

    private static bool FilesMatch(string source, string destination, long sourceLength) =>
        new FileInfo(destination).Length == sourceLength &&
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(File.ReadAllBytes(source)),
            SHA256.HashData(File.ReadAllBytes(destination)));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
