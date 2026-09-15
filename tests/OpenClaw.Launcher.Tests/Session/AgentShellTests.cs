using System.Text;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class AgentShellTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public void InstallCreatesAnAsciiDynamicCommandShim()
    {
        AgentTools tools = AgentToolShim.Install(_root);
        byte[] bytes = File.ReadAllBytes(tools.ShimPath);
        string content = Encoding.ASCII.GetString(bytes);

        Assert.Equal(AgentToolShim.ShimContent, content);
        Assert.All(bytes, value => Assert.InRange(value, (byte)0, (byte)127));
        Assert.Contains("%OPENCLAW_SHIM_NODE%", content, StringComparison.Ordinal);
        Assert.Contains("%OPENCLAW_SHIM_ENTRY%", content, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallRejectsAReparsePointBeforeHostWrite()
    {
        string tools = Path.Combine(_root, AgentToolShim.DirectoryName);
        var fileSystem = new RecordingAgentToolFileSystem(
            _root,
            tools,
            FileAttributes.Directory | FileAttributes.ReparsePoint);

        SessionException exception = Assert.Throws<SessionException>(
            () => AgentToolShim.Install(_root, fileSystem));

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fileSystem.CreateCount);
        Assert.Equal(0, fileSystem.WriteCount);
    }

    [Fact]
    public void PowerShell7IsPreferredBeforePreviewAndWindowsPowerShell()
    {
        AgentShell shell = AgentShellResolver.Resolve(
            path => path == AgentShellResolver.PowerShell7Paths[0] ||
                path == AgentShellResolver.PowerShell7Paths[1]);

        Assert.Equal(AgentShellResolver.PowerShell7Paths[0], shell.ExecutablePath);
        Assert.Equal("PowerShell 7", shell.DisplayName);
    }

    [Fact]
    public void WindowsPowerShellIsUsedWhenNoMachineWidePowerShell7Exists()
    {
        AgentShell shell = AgentShellResolver.Resolve(_ => false);

        Assert.Equal(AgentShellResolver.WindowsPowerShellPath, shell.ExecutablePath);
        Assert.Equal("Windows PowerShell", shell.DisplayName);
    }

    [Fact]
    public void ShellPreparationPutsToolsThenAgentNodeAheadOfExistingPath()
    {
        IReadOnlyList<string> arguments = AgentShellResolver.BuildArguments(
            @"C:\shared workspace",
            "agent's name",
            @"C:\shared workspace\.openclaw-tools",
            @"C:\Users\agent\AppData\Local\OpenClaw\NodeJS\node-v24");

        Assert.Equal(["-NoLogo", "-NoProfile", "-NoExit", "-Command"], arguments.Take(4));
        string preparation = arguments[4];
        Assert.Contains(
            @"C:\shared workspace\.openclaw-tools' + [IO.Path]::PathSeparator + 'C:\Users\agent",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains("agent''s name", preparation, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingAgentToolFileSystem(
        string root,
        string redirectedPath,
        FileAttributes redirectedAttributes) : IAgentToolFileSystem
    {
        public int CreateCount { get; private set; }
        public int WriteCount { get; private set; }

        public FileAttributes GetAttributes(string path)
        {
            if (path.Equals(redirectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return redirectedAttributes;
            }

            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return FileAttributes.Directory;
            }

            throw new FileNotFoundException();
        }

        public void CreateDirectory(string path) => CreateCount++;
        public bool FileExists(string path) => false;
        public string ReadAllText(string path, Encoding encoding) => throw new IOException();
        public void WriteAllText(string path, string content, Encoding encoding) => WriteCount++;
    }
}
