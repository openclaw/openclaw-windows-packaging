using System.Text;

namespace OpenClaw.Launcher.Session;

/// <summary>Where the agent's <c>openclaw</c> command lives.</summary>
internal sealed record AgentTools(string DirectoryPath, string ShimPath);

/// <summary>Installs the dynamic command shim used inside the agent session.</summary>
internal static class AgentToolShim
{
    internal const string DirectoryName = ".openclaw-tools";
    internal const string ShimFileName = "openclaw.cmd";
    internal const string NodeVariable = "OPENCLAW_SHIM_NODE";
    internal const string EntryPointVariable = "OPENCLAW_SHIM_ENTRY";

    internal const string ShimContent =
        "@echo off\r\n" +
        "setlocal\r\n" +
        "if not defined " + NodeVariable + " goto :missing\r\n" +
        "if not defined " + EntryPointVariable + " goto :missing\r\n" +
        "\"%" + NodeVariable + "%\" \"%" + EntryPointVariable + "%\" %*\r\n" +
        "exit /b %ERRORLEVEL%\r\n" +
        ":missing\r\n" +
        "echo openclaw: this shim only runs inside an OpenClaw agent session.>&2\r\n" +
        "exit /b 9009\r\n";

    public static AgentTools Install(string workspacePath) =>
        Install(workspacePath, PhysicalAgentToolFileSystem.Instance);

    internal static AgentTools Install(
        string workspacePath,
        IAgentToolFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(fileSystem);

        string workspace = Path.GetFullPath(workspacePath);
        string directory = Path.GetFullPath(Path.Combine(workspace, DirectoryName));
        string shimPath = Path.Combine(directory, ShimFileName);
        try
        {
            TrustedPath.EnsureNoReparsePoints(
                workspace, directory, fileSystem.GetAttributes);
            fileSystem.CreateDirectory(directory);
            TrustedPath.EnsureNoReparsePoints(
                workspace, shimPath, fileSystem.GetAttributes);
            if (!IsCurrent(shimPath, fileSystem))
            {
                fileSystem.WriteAllText(shimPath, ShimContent, Encoding.ASCII);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new SessionException(
                "The agent's `openclaw` command could not be installed in the " +
                $"shared workspace: {exception.Message}",
                exception);
        }

        return new AgentTools(directory, shimPath);
    }

    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        string nodePath,
        string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NodeVariable] = nodePath,
            [EntryPointVariable] = Path.Combine(applicationDirectory, "openclaw.mjs"),
        };
    }

    private static bool IsCurrent(string shimPath, IAgentToolFileSystem fileSystem)
    {
        if (!fileSystem.FileExists(shimPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                fileSystem.ReadAllText(shimPath, Encoding.ASCII),
                ShimContent,
                StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }
}

internal interface IAgentToolFileSystem
{
    FileAttributes GetAttributes(string path);
    void CreateDirectory(string path);
    bool FileExists(string path);
    string ReadAllText(string path, Encoding encoding);
    void WriteAllText(string path, string content, Encoding encoding);
}

internal sealed class PhysicalAgentToolFileSystem : IAgentToolFileSystem
{
    public static PhysicalAgentToolFileSystem Instance { get; } = new();

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool FileExists(string path) => File.Exists(path);
    public string ReadAllText(string path, Encoding encoding) =>
        File.ReadAllText(path, encoding);
    public void WriteAllText(string path, string content, Encoding encoding) =>
        File.WriteAllText(path, content, encoding);
}
