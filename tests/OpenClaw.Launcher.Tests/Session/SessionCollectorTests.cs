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

    private string Workspace => Path.Combine(_root, "workspace");

    private string Destination => Path.Combine(Workspace, "staged");

    private string RequestPath => Path.Combine(Workspace, "collect.json");

    private static IReadOnlyList<string> Denied =>
        ["openclaw-agent.sqlite*", "openclaw.sqlite*"];

    private string Write(string relativePath, string content)
    {
        string path = Path.Combine(Source, relativePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private SessionCollectResult Run(params SessionCollectSource[] sources)
    {
        return Run(beforeDestinationOpen: null, sources);
    }

    private SessionCollectResult Run(
        Action? beforeDestinationOpen,
        params SessionCollectSource[] sources)
    {
        var request = new SessionCollectRequest
        {
            RequestId = "c1",
            DestinationDirectory = Destination,
            Sources = sources,
            DeniedNames = Denied
        };
        _ = Directory.CreateDirectory(Workspace);
        File.WriteAllText(RequestPath, SessionCollectProtocol.SerializeRequest(request));

        int exitCode = SessionCollector.Run(
            RequestPath,
            File.ReadAllText,
            File.WriteAllText,
            Source,
            beforeDestinationOpen: beforeDestinationOpen);
        Assert.Equal(0, exitCode);

        return SessionCollectProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(RequestPath)));
    }

    [Fact]
    public void NamedFilesAreStagedUnderTheRequestedName()
    {
        _ = Write("logs\\openclaw.log", "hello-from-the-agent");

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = @"logs\openclaw.log",
            Name = "agent/openclaw.log"
        });

        Assert.Equal(
            "hello-from-the-agent",
            File.ReadAllText(Path.Combine(Destination, "agent", "openclaw.log")));
        SessionCollectEntry entry = Assert.Single(result.Entries!);
        Assert.True(entry.Copied, entry.Detail);
    }

    [Fact]
    public void DirectoriesAreStagedRecursivelyWhenRequested()
    {
        _ = Write("logs\\openclaw.log", "top");
        _ = Write("logs\\nested\\worker.log", "nested");

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
        _ = Write($"agent\\{fileName}", "secret-bearing");
        _ = Write("agent\\openclaw.log", "safe");

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

    [Fact]
    public void SourceLinksAreRejectedBeforeReadingTheirTargets()
    {
        string target = Write("agent\\openclaw.sqlite", "secret-bearing");
        string link = Path.Combine(Source, "logs", "safe.log");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = @"logs\safe.log",
            Name = "safe.log"
        });

        SessionCollectEntry entry = Assert.Single(result.Entries!);
        Assert.False(entry.Copied);
        Assert.Contains("filesystem link", entry.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Destination, "safe.log")));
    }

    [Fact]
    public void DestinationLinksAreRejectedBeforeWritingOutsideStaging()
    {
        _ = Write("logs\\openclaw.log", "safe");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Destination);
        Directory.CreateSymbolicLink(Path.Combine(Destination, "agent"), outside);

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = @"logs\openclaw.log",
            Name = "agent/openclaw.log"
        });

        SessionCollectEntry entry = Assert.Single(result.Entries!);
        Assert.False(entry.Copied);
        Assert.Contains("filesystem link", entry.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outside, "openclaw.log")));
    }

    [Fact]
    public void DestinationAuthorityPreventsReplacementBeforeOpeningTheFile()
    {
        _ = Write("logs\\openclaw.log", "safe");
        string outside = Path.Combine(_root, "outside");
        string destinationDirectory = Path.Combine(Destination, "agent");
        string relocatedDirectory = Path.Combine(outside, "agent");
        string outsideFile = Path.Combine(relocatedDirectory, "sentinel.log");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(
            Path.Combine(destinationDirectory, "sentinel.log"),
            "sentinel");
        Exception? replacementFailure = null;

        SessionCollectResult result = Run(
            () =>
            {
                try
                {
                    Directory.Move(destinationDirectory, relocatedDirectory);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    replacementFailure = exception;
                }
            },
            new SessionCollectSource
            {
                RelativePath = @"logs\openclaw.log",
                Name = "agent/openclaw.log"
            });

        SessionCollectEntry entry = Assert.Single(result.Entries!);
        if (replacementFailure is null)
        {
            Assert.False(entry.Copied);
            Assert.Equal("sentinel", File.ReadAllText(outsideFile));
        }
        else
        {
            Assert.True(entry.Copied, entry.Detail);
            Assert.False(File.Exists(outsideFile));
            Assert.Equal(
                "safe",
                File.ReadAllText(Path.Combine(destinationDirectory, "openclaw.log")));
        }
    }

    [Fact]
    public void ExistingHardLinkedOutputIsRejectedWithoutChangingItsTarget()
    {
        _ = Write("logs\\openclaw.log", "safe");
        string outside = Path.Combine(_root, "outside");
        string sentinel = Path.Combine(outside, "sentinel.log");
        string output = Path.Combine(Destination, "agent", "openclaw.log");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Directory.CreateDirectory(outside);
        File.WriteAllText(sentinel, "sentinel");
        Assert.True(
            CreateHardLink(output, sentinel, IntPtr.Zero),
            new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message);

        SessionCollectResult result = Run(new SessionCollectSource
        {
            RelativePath = @"logs\openclaw.log",
            Name = "agent/openclaw.log"
        });

        Assert.Equal("sentinel", File.ReadAllText(sentinel));
        Assert.Equal("sentinel", File.ReadAllText(output));
        SessionCollectEntry entry = Assert.Single(result.Entries!);
        Assert.False(entry.Copied);
    }

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

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

        SessionCollectEntry entry = Assert.Single(result.Entries!);
        Assert.True(entry.Copied, entry.Detail);
        Assert.Equal(
            "live-gateway-output",
            File.ReadAllText(Path.Combine(Destination, "gateway.log")));
    }

    [Fact]
    public void AnUnreadableRequestIsReportedThroughTheResultFile()
    {
        _ = Directory.CreateDirectory(Workspace);
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

    [Fact]
    public void DestinationOutsideTheSharedWorkspaceIsRejected()
    {
        string outside = Path.Combine(_root, "outside");
        string sentinel = Path.Combine(outside, "sentinel.txt");
        _ = Directory.CreateDirectory(Workspace);
        _ = Directory.CreateDirectory(outside);
        File.WriteAllText(sentinel, "unchanged");
        {
            var request = new SessionCollectRequest
            {
                RequestId = "c1",
                DestinationDirectory = outside,
                Sources = []
            };
            File.WriteAllText(RequestPath, SessionCollectProtocol.SerializeRequest(request));

            int exitCode = SessionCollector.Run(
                RequestPath, File.ReadAllText, File.WriteAllText, Source);

            Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
            SessionCollectResult result = SessionCollectProtocol.ReadResult(
                File.ReadAllText(SessionLaunchProtocol.ResultPathFor(RequestPath)));
            Assert.Equal("destination resolves outside the shared workspace", result.Error);
            Assert.Equal("unchanged", File.ReadAllText(sentinel));
        }
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
        _ = Write("logs\\openclaw.log", "agent-side");

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
        _ = Write("config\\openclaw.json", "live");
        _ = Write("config\\openclaw.json.bak", "previous");
        _ = Write("config\\completions.ps1", "enormous");

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

    [Fact]
    public void DirectoryEnumerationFailureIsReportedInsteadOfFailingCollection()
    {
        _ = Directory.CreateDirectory(Path.Combine(Source, "logs"));
        var request = new SessionCollectRequest
        {
            RequestId = "c1",
            DestinationDirectory = Destination,
            Sources = [new SessionCollectSource
            {
                RelativePath = "logs",
                Name = "logs",
                Recursive = true
            }]
        };
        _ = Directory.CreateDirectory(Workspace);
        File.WriteAllText(RequestPath, SessionCollectProtocol.SerializeRequest(request));
        string retained = Write("logs\\retained.log", "retained");

        int exitCode = SessionCollector.Run(
            RequestPath,
            File.ReadAllText,
            File.WriteAllText,
            Source,
            (_, _, _) => ThrowAfterYield(retained));

        Assert.Equal(0, exitCode);
        SessionCollectResult result = SessionCollectProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(RequestPath)));
        Assert.Collection(
            result.Entries!,
            entry =>
            {
                Assert.True(entry.Copied);
                Assert.Equal("logs/retained.log", entry.Name);
                Assert.Equal("retained", File.ReadAllText(Path.Combine(Destination, "logs", "retained.log")));
            },
            entry =>
            {
                Assert.False(entry.Copied);
                Assert.Equal("logs", entry.Name);
                Assert.StartsWith("unreadable:", entry.Detail, StringComparison.Ordinal);
            });

        static IEnumerable<string> ThrowAfterYield(string first)
        {
            yield return first;
            throw new UnauthorizedAccessException("fixture access denied");
        }
    }
}
