using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Drives the real guest collector against real files, because its whole job is
/// copying files the host cannot otherwise reach.
/// </summary>
public sealed class SessionCollectorTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Source => Path.Combine(_root, "profile");

    private string Destination => Path.Combine(_root, "staged");

    private string RequestPath => Path.Combine(_root, "collect.json");

    private static IReadOnlyList<string> Denied =>
        ["openclaw-agent.sqlite*", "openclaw.sqlite*"];

    private string Write(string relativePath, string content)
    {
        string path = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private SessionCollectResult Run(params SessionCollectSource[] sources)
    {
        var request = new SessionCollectRequest
        {
            RequestId = "c1",
            DestinationDirectory = Destination,
            Sources = sources,
            DeniedNames = Denied
        };
        File.WriteAllText(RequestPath, SessionCollectProtocol.SerializeRequest(request));

        int exitCode = SessionCollector.Run(
            RequestPath, File.ReadAllText, File.WriteAllText, Source);
        Assert.Equal(0, exitCode);

        return SessionCollectProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(RequestPath)));
    }

    [Fact]
    public void NamedFilesAreStagedUnderTheRequestedName()
    {
        Write("logs\\openclaw.log", "hello-from-the-agent");

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = @"logs\openclaw.log",
            Name = "agent/openclaw.log"
        });

        Assert.Equal(
            "hello-from-the-agent",
            File.ReadAllText(Path.Combine(Destination, "agent", "openclaw.log")));
        Assert.True(Assert.Single(result.Entries!).Copied);
    }

    [Fact]
    public void DirectoriesAreStagedRecursivelyWhenRequested()
    {
        Write("logs\\openclaw.log", "top");
        Write("logs\\nested\\worker.log", "nested");

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = "logs",
            Name = "agent-logs",
            Recursive = true
        });

        Assert.Equal("top", File.ReadAllText(Path.Combine(Destination, "agent-logs", "openclaw.log")));
        Assert.Equal(
            "nested",
            File.ReadAllText(Path.Combine(Destination, "agent-logs", "nested", "worker.log")));
        Assert.Equal(2, result.Entries!.Count);
    }

    // The credential stores sit in the same directory as the logs worth
    // collecting, so the deny list is the only thing keeping them out.
    [Theory]
    [InlineData("openclaw-agent.sqlite")]
    [InlineData("openclaw-agent.sqlite-wal")]
    [InlineData("openclaw-agent.sqlite-shm")]
    public void CredentialStoresAreNeverStaged(string fileName)
    {
        Write($"agent\\{fileName}", "secret-bearing");
        Write("agent\\openclaw.log", "safe");

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = "agent",
            Name = "agent",
            Recursive = true
        });

        Assert.False(File.Exists(Path.Combine(Destination, "agent", fileName)));
        Assert.True(File.Exists(Path.Combine(Destination, "agent", "openclaw.log")));
        Assert.Contains(
            result.Entries!,
            entry => entry.Name!.EndsWith(fileName, StringComparison.Ordinal) &&
                !entry.Copied &&
                entry.Detail == "excluded by policy");
    }

    // Collection exists for broken installations, so an absent source has to be
    // reported rather than treated as a failure.
    [Fact]
    public void MissingSourcesAreReportedWithoutFailing()
    {
        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = "does-not-exist.log",
            Name = "missing.log"
        });

        SessionCollectEntry entry = Assert.Single(result.Entries!);
        Assert.False(entry.Copied);
        Assert.Equal("not present", entry.Detail);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FilesHeldOpenByTheAgentAreStillStaged()
    {
        string path = Write("logs\\gateway.log", "live-gateway-output");
        using var held = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = @"logs\gateway.log",
            Name = "gateway.log"
        });

        Assert.True(Assert.Single(result.Entries!).Copied);
        Assert.Equal(
            "live-gateway-output",
            File.ReadAllText(Path.Combine(Destination, "gateway.log")));
    }

    [Fact]
    public void AnUnreadableRequestIsReportedThroughTheResultFile()
    {
        File.WriteAllText(RequestPath, "{ not json");

        int exitCode = SessionCollector.Run(
            RequestPath, File.ReadAllText, File.WriteAllText);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        SessionCollectResult result = SessionCollectProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(RequestPath)));
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("..\\escape.log")]
    [InlineData("C:\\Windows\\escape.log")]
    public void SourceNamesThatEscapeTheStagingDirectoryAreRejected(string name)
    {
        var request = new SessionCollectRequest
        {
            RequestId = "c1",
            DestinationDirectory = Destination,
            Sources = [new SessionCollectSource { RelativePath = "a.log", Name = name }]
        };

        Assert.Throws<SessionLaunchException>(() =>
            SessionCollectProtocol.ReadRequest(
                SessionCollectProtocol.SerializeRequest(request)));
    }

    // The host cannot name the agent's profile: the account is generated and
    // its directory is ACL'd against the host user. An absolute path built on
    // the host names the host's own profile, which is how a bundle came back
    // reporting every agent source as "not present".
    [Theory]
    [InlineData(@"C:\Users\someone-else\.openclaw")]
    [InlineData(@"..\..\Windows")]
    public void SourcesThatLeaveTheAgentProfileAreRejected(string relativePath)
    {
        var request = new SessionCollectRequest
        {
            RequestId = "c1",
            DestinationDirectory = Destination,
            Sources = [new SessionCollectSource { RelativePath = relativePath, Name = "x" }]
        };

        Assert.Throws<SessionLaunchException>(() =>
            SessionCollectProtocol.ReadRequest(
                SessionCollectProtocol.SerializeRequest(request)));
    }

    // Only the guest knows where the agent profile is, so the same request has
    // to resolve to whichever profile it runs under.
    [Fact]
    public void SourcesResolveAgainstTheProfileTheGuestRunsUnder()
    {
        Write("logs\\openclaw.log", "agent-side");

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = @"logs\openclaw.log",
            Name = "openclaw.log"
        });

        Assert.True(Assert.Single(result.Entries!).Copied);
        Assert.Equal(
            "agent-side",
            File.ReadAllText(Path.Combine(Destination, "openclaw.log")));
    }

    // The directories worth collecting also hold megabytes of shell completions
    // and the agent's own workspace content, none of which belongs in a bundle
    // handed to someone else.
    [Fact]
    public void ADirectorySourceTakesOnlyItsPattern()
    {
        Write("config\\openclaw.json", "live");
        Write("config\\openclaw.json.bak", "previous");
        Write("config\\completions.ps1", "enormous");

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = "config",
            Name = "config",
            Pattern = "openclaw.json*"
        });

        Assert.True(File.Exists(Path.Combine(Destination, "config", "openclaw.json")));
        Assert.True(File.Exists(Path.Combine(Destination, "config", "openclaw.json.bak")));
        Assert.False(File.Exists(Path.Combine(Destination, "config", "completions.ps1")));
        Assert.Equal(2, result.Entries!.Count);
    }
}
