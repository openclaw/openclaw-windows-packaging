namespace OpenClaw.Launcher.Session;

internal static class SessionCompletionProjection
{
    private static readonly string[] RelativePath = [".openclaw", "cache", "completion.ps1"];

    internal static string? Project(
        SessionWorkspaceOperation operation,
        string cachePath)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);

        string destination = Path.Combine([operation.WorkspacePath, .. RelativePath]);
        if (!File.Exists(cachePath))
        {
            operation.Delete(destination);
            return null;
        }

        string directory = Path.GetDirectoryName(destination)
            ?? throw new SessionException(
                $"The completion projection has no parent directory: {destination}");
        operation.EnsureDirectory(directory);
        operation.Delete(destination);
        using FileStream input = File.OpenRead(cachePath);
        using Stream output = operation.CreateNew(destination);
        input.CopyTo(output);
        return destination;
    }
}
