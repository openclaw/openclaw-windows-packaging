using System.Diagnostics;
using System.Text;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class WorkspaceEnvironmentInstructionsTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ApplyRunsUpstreamSetupAndCreatesManagedInstructions()
    {
        string profile = Path.Combine(_root, "agent");
        string workspace = Path.Combine(profile, ".openclaw", "workspace");
        Directory.CreateDirectory(profile);
        ProcessStartInfo? observed = null;

        (string actualWorkspace, bool updated) = WorkspaceEnvironmentInstructions.Apply(
            @"C:\agent\node.exe",
            @"C:\Package\app",
            @"C:\Package\node\native-redirect.mjs",
            @"C:\agent\native",
            new Dictionary<string, string>
            {
                ["OPENCLAW_SUPERVISOR_MODE"] = "external",
                ["PATH"] = @"C:\agent\tools;C:\Windows\System32"
            },
            startInfo =>
            {
                observed = startInfo;
                return new OpenClawSetupResult(
                    0,
                    $$"""{"ok":true,"workspaceDir":"{{JsonPath(workspace)}}"}""",
                    string.Empty);
            },
            () => profile);

        Assert.True(updated);
        Assert.Equal(workspace, actualWorkspace);
        Assert.NotNull(observed);
        Assert.Equal(
            [
                "--import",
                new Uri(@"C:\Package\node\native-redirect.mjs").AbsoluteUri,
                @"C:\Package\app\openclaw.mjs",
                "setup",
                "--baseline",
                "--json"
            ],
            observed.ArgumentList);
        Assert.Equal("external", observed.Environment["OPENCLAW_SUPERVISOR_MODE"]);
        Assert.Equal(@"C:\agent\native", observed.Environment["OPENCLAW_NATIVE_STAGED_ROOT"]);
        Assert.Equal(
            @"C:\agent;C:\agent\tools;C:\Windows\System32",
            observed.Environment["PATH"]);

        string agents = File.ReadAllText(Path.Combine(workspace, "AGENTS.md"));
        Assert.Contains(
            WorkspaceEnvironmentInstructions.StartMarker,
            agents,
            StringComparison.Ordinal);
        Assert.Contains("no interactive Windows desktop", agents, StringComparison.Ordinal);
        Assert.Contains(
            WorkspaceEnvironmentInstructions.EndMarker,
            agents,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatePreservesUserContentNewlinesAndUtf8Preamble()
    {
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        string path = Path.Combine(workspace, "AGENTS.md");
        string original = "# User rules\r\n\r\nKeep this.\r\n";
        File.WriteAllText(path, original, new UTF8Encoding(true));

        Assert.True(WorkspaceEnvironmentInstructions.UpdateAgentsFile(workspace));
        Assert.False(WorkspaceEnvironmentInstructions.UpdateAgentsFile(workspace));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        string actual = File.ReadAllText(path);
        Assert.StartsWith(original, actual, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\n",
            actual.Replace("\r\n", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            Count(actual, WorkspaceEnvironmentInstructions.StartMarker));
    }

    [Fact]
    public void UpdateReplacesOnlyTheManagedSection()
    {
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        string path = Path.Combine(workspace, "AGENTS.md");
        File.WriteAllText(
            path,
            $"before\n{WorkspaceEnvironmentInstructions.StartMarker}\nold\n" +
            $"{WorkspaceEnvironmentInstructions.EndMarker}\nafter");

        Assert.True(WorkspaceEnvironmentInstructions.UpdateAgentsFile(workspace));

        string actual = File.ReadAllText(path);
        Assert.StartsWith("before\n", actual, StringComparison.Ordinal);
        Assert.EndsWith("\nafter", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("\nold\n", actual, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!-- openclaw-windows-packaging:environment:start -->")]
    [InlineData("<!-- openclaw-windows-packaging:environment:end -->")]
    [InlineData(
        "<!-- openclaw-windows-packaging:environment:start -->\n" +
        "<!-- openclaw-windows-packaging:environment:start -->\n" +
        "<!-- openclaw-windows-packaging:environment:end -->")]
    public void MalformedMarkersFailWithoutChangingTheFile(string content)
    {
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        string path = Path.Combine(workspace, "AGENTS.md");
        File.WriteAllText(path, content);

        Assert.Throws<SessionLaunchException>(
            () => WorkspaceEnvironmentInstructions.UpdateAgentsFile(workspace));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void InvalidUtf8FailsWithoutChangingTheFile()
    {
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        string path = Path.Combine(workspace, "AGENTS.md");
        byte[] content = [0xff, 0xfe, 0xfd];
        File.WriteAllBytes(path, content);

        Assert.Throws<SessionLaunchException>(
            () => WorkspaceEnvironmentInstructions.UpdateAgentsFile(workspace));
        Assert.Equal(content, File.ReadAllBytes(path));
    }

    [Fact]
    public void ApplyPreservesAnExternalWorkspace()
    {
        string profile = Path.Combine(_root, "agent");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(outside);
        string agentsPath = Path.Combine(outside, "AGENTS.md");
        File.WriteAllText(agentsPath, "# Existing instructions\n\nKeep this.\n");

        (string workspace, bool updated) = WorkspaceEnvironmentInstructions.Apply(
            @"C:\agent\node.exe",
            @"C:\Package\app",
            @"C:\Package\node\native-redirect.mjs",
            null,
            new Dictionary<string, string>(),
            _ => new OpenClawSetupResult(
                0,
                $$"""{"workspaceDir":"{{JsonPath(outside)}}"}""",
                string.Empty),
            () => profile);

        Assert.True(updated);
        Assert.Equal(outside, workspace);
        Assert.StartsWith(
            "# Existing instructions\n\nKeep this.\n",
            File.ReadAllText(agentsPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyRejectsRelativeWorkspacePath()
    {
        string profile = Path.Combine(_root, "agent");
        Directory.CreateDirectory(profile);

        SessionLaunchException exception = Assert.Throws<SessionLaunchException>(
            () => WorkspaceEnvironmentInstructions.Apply(
                @"C:\agent\node.exe",
                @"C:\Package\app",
                @"C:\Package\node\native-redirect.mjs",
                null,
                new Dictionary<string, string>(),
                _ => new OpenClawSetupResult(
                    0,
                    """{"workspaceDir":"relative-workspace"}""",
                    string.Empty),
                () => profile));

        Assert.Contains("fully qualified", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(profile, "relative-workspace")));
    }

    [Fact]
    public void ReparsePointInWorkspacePathIsRejected()
    {
        string reparseDirectory = Path.Combine(_root, "shared");
        string workspace = Path.Combine(reparseDirectory, "workspace");

        SessionLaunchException exception = Assert.Throws<SessionLaunchException>(
            () => WorkspaceEnvironmentInstructions.EnsureNoReparsePoints(
                workspace,
                _ => true,
                path => string.Equals(
                    path,
                    reparseDirectory,
                    StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Directory | FileAttributes.ReparsePoint
                    : FileAttributes.Directory));

        Assert.Contains("reparse point", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyReportsUpstreamFailureWithoutWritingAWorkspace()
    {
        string profile = Path.Combine(_root, "agent");
        Directory.CreateDirectory(profile);

        SessionLaunchException exception = Assert.Throws<SessionLaunchException>(
            () => WorkspaceEnvironmentInstructions.Apply(
                @"C:\agent\node.exe",
                @"C:\Package\app",
                @"C:\Package\node\native-redirect.mjs",
                null,
                new Dictionary<string, string>(),
                _ => new OpenClawSetupResult(1, string.Empty, "config is invalid"),
                () => profile));

        Assert.Contains("config is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(profile));
    }

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;

    private static string JsonPath(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal);
}
