using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionHelperStagerTests : IDisposable
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
    public void StageCopiesThePackagedHelperToAFullVersionedWorkspacePath()
    {
        string source = Path.Combine(_root, "package", "openclaw-session-host.exe");
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(source, "helper bytes");

        string staged = SessionHelperStager.Stage(source, workspace);

        Assert.True(Path.IsPathFullyQualified(staged));
        Assert.StartsWith(
            Path.GetFullPath(workspace),
            staged,
            StringComparison.Ordinal);
        Assert.Equal("helper bytes", File.ReadAllText(staged));
        Assert.Equal(
            staged,
            SessionHelperStager.RequireStaged(source, workspace));
    }

    [Fact]
    public void StageReplacesSameLengthDifferentHelper()
    {
        string source = Path.Combine(_root, "package", "openclaw-session-host.exe");
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(source, "helper-v2");
        string stagedPath = SessionHelperStager.ResolveStagedPath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
        File.WriteAllText(stagedPath, "helper-v1");

        string staged = SessionHelperStager.Stage(source, workspace);

        Assert.Equal(stagedPath, staged);
        Assert.Equal("helper-v2", File.ReadAllText(staged));
    }

    [Fact]
    public void RequireStagedDirectsTheUserBackToSetup()
    {
        string source = Path.Combine(_root, "openclaw-session-host.exe");
        File.WriteAllText(source, "helper bytes");

        SessionException failure = Assert.Throws<SessionException>(
            () => SessionHelperStager.RequireStaged(
                source,
                Path.Combine(_root, "workspace")));

        Assert.Contains("clawctl setup", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPackagedHelperIsReportedBeforeStaging()
    {
        SessionException failure = Assert.Throws<SessionException>(
            () => SessionHelperStager.Stage(
                Path.Combine(_root, "missing.exe"),
                Path.Combine(_root, "workspace")));

        Assert.Contains(
            "packaged session helper is missing",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreatedFileRetainsDirectoryAuthorityUntilTheWriteCompletes()
    {
        string workspace = Path.Combine(_root, "workspace");
        string stagingDirectory = Path.Combine(workspace, "staging");
        string relocatedDirectory = Path.Combine(_root, "outside", "staging");
        string destination = Path.Combine(stagingDirectory, "helper.exe");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(relocatedDirectory)!);
        var record = new SessionRecord
        {
            SandboxId = "iso:test",
            ApplicationId = "test",
            WorkspacePath = workspace,
            Generation = "generation"
        };
        using var operation = new SessionWorkspaceOperation(record, _ => true);
        operation.EnsureDirectory(stagingDirectory);

        using (Stream output = operation.CreateNew(destination))
        {
            _ = Assert.ThrowsAny<IOException>(
                () => Directory.Move(stagingDirectory, relocatedDirectory));
            output.Write("helper bytes"u8);
        }

        Assert.False(File.Exists(Path.Combine(relocatedDirectory, "helper.exe")));
        Assert.Equal("helper bytes", File.ReadAllText(destination));
    }
}
