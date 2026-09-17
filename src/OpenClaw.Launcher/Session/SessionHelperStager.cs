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
        using SessionWorkspaceOperation operation = CreateOperation(workspacePath);
        string destination = ResolveStagedPath(operation.WorkspacePath);

        if (File.Exists(destination))
        {
            using FileStream existing = operation.OpenRead(destination);
            if (FilesMatch(source, existing))
            {
                return destination;
            }
        }

        string? destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new SessionException(
                $"The staged helper path has no parent directory: {destination}");
        }

        operation.EnsureDirectory(destinationDirectory);
        try
        {
            operation.Delete(destination);
            using FileStream input = File.OpenRead(source);
            using Stream output = operation.CreateNew(destination);
            input.CopyTo(output);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new SessionException(
                "The packaged session helper could not be staged into the " +
                $"shared workspace: {exception.Message}",
                exception);
        }

        return destination;
    }

    public static string RequireStaged(
        string packagedHelperPath,
        string workspacePath)
    {
        string source = RequirePackagedHelper(packagedHelperPath);
        try
        {
            using SessionWorkspaceOperation operation = CreateOperation(workspacePath);
            string path = ResolveStagedPath(operation.WorkspacePath);
            using FileStream staged = operation.OpenRead(path);
            return FilesMatch(source, staged)
                ? path
                : throw new SessionException(
                    "The isolated-session helper does not match this package version. " +
                    "Run `clawctl setup` again.");
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or SessionException)
        {
            throw new SessionException(
                "The isolated-session helper has not been staged for this " +
                "package version. Run `clawctl setup` again.",
                exception);
        }
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

    private static SessionWorkspaceOperation CreateOperation(string workspacePath)
    {
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
        return new SessionWorkspaceOperation(stagingRecord, _ => true);
    }

    private static bool FilesMatch(string source, Stream destination) =>
        new FileInfo(source).Length == destination.Length &&
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(File.ReadAllBytes(source)),
            SHA256.HashData(destination));
}
