using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionCompletionProjectionTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ProjectCreatesCompletionUnderTheValidatedWorkspace()
    {
        string cache = CreateCache("trusted completion");
        string workspace = CreateWorkspace();
        using var operation = CreateOperation(workspace, () => true);

        string? projected = SessionCompletionProjection.Project(operation, cache);

        Assert.Equal(
            Path.Combine(workspace, ".openclaw", "cache", "completion.ps1"),
            projected);
        Assert.Equal("trusted completion", File.ReadAllText(projected!));
    }

    [Fact]
    public void ProjectRemovesStaleCompletionWhenTheHostCacheIsAbsent()
    {
        string workspace = CreateWorkspace();
        string projected = Path.Combine(workspace, ".openclaw", "cache", "completion.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(projected)!);
        File.WriteAllText(projected, "stale completion");
        using var operation = CreateOperation(workspace, () => true);

        string? result = SessionCompletionProjection.Project(
            operation,
            Path.Combine(_root, "missing.ps1"));

        Assert.Null(result);
        Assert.False(File.Exists(projected));
    }

    [Theory]
    [InlineData(".openclaw")]
    [InlineData(@".openclaw\cache")]
    public void ProjectRejectsRedirectedDirectory(string redirectedDirectory)
    {
        string cache = CreateCache("trusted completion");
        string workspace = CreateWorkspace();
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string redirectedPath = Path.Combine(workspace, redirectedDirectory);
        string? parent = Path.GetDirectoryName(redirectedPath);
        if (!string.Equals(parent, workspace, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(parent!);
        }
        Directory.CreateSymbolicLink(
            redirectedPath,
            outside);
        using var operation = CreateOperation(workspace, () => true);

        Assert.Throws<SessionException>(
            () => SessionCompletionProjection.Project(operation, cache));
        Assert.False(File.Exists(Path.Combine(outside, "completion.ps1")));
    }

    [Fact]
    public void ProjectRejectsChangedSessionGenerationBeforeWriting()
    {
        string cache = CreateCache("trusted completion");
        string workspace = CreateWorkspace();
        bool isCurrent = true;
        using var operation = CreateOperation(workspace, () => isCurrent);
        isCurrent = false;

        Assert.Throws<SessionException>(
            () => SessionCompletionProjection.Project(operation, cache));
        Assert.False(Directory.Exists(Path.Combine(workspace, ".openclaw")));
    }

    [Fact]
    public void ProjectReplacesRedirectedLeafWithoutWritingToItsTarget()
    {
        string cache = CreateCache("trusted completion");
        string workspace = CreateWorkspace();
        string projected = Path.Combine(workspace, ".openclaw", "cache", "completion.ps1");
        string outside = Path.Combine(_root, "outside.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(projected)!);
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(projected, outside);
        using var operation = CreateOperation(workspace, () => true);

        string? result = SessionCompletionProjection.Project(operation, cache);

        Assert.Equal(projected, result);
        Assert.Equal("outside", File.ReadAllText(outside));
        Assert.Equal("trusted completion", File.ReadAllText(projected));
        Assert.False((File.GetAttributes(projected) & FileAttributes.ReparsePoint) != 0);
    }

    private string CreateCache(string contents)
    {
        string cache = Path.Combine(_root, "host", "openclaw.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, contents);
        return cache;
    }

    private string CreateWorkspace()
    {
        string workspace = Path.Combine(_root, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static SessionWorkspaceOperation CreateOperation(
        string workspace,
        Func<bool> isCurrent) =>
        new(
            new SessionRecord
            {
                SandboxId = "sandbox",
                WorkspacePath = workspace,
                Generation = "generation"
            },
            _ => isCurrent());
}
