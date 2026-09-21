using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionLaunchProtocolTests
{
    private static SessionLaunchRequest Valid() => new()
    {
        RequestId = "r-1",
        Executable = @"C:\Program Files\nodejs\node.exe",
        Arguments = [@"C:\app\openclaw.mjs", "--help"],
        WorkingDirectory = @"C:\work"
    };

    [Fact]
    public void AHostileArgumentVectorSurvivesTheRoundTripByteForByte()
    {
        // These are the exact cases that cmd.exe corrupted when the vector was
        // passed as a command line: a quote in one argument truncated a later
        // one, and %USERPROFILE% was expanded before the process saw it.
        string[] hostile =
        [
            "%USERPROFILE%",
            "q\"x",
            "a&b",
            "has spaces",
            "trailing\\",
            "pipe|caret^semi;",
            "(parens)",
            "bang!",
            string.Empty,
            "unicode-\u00e9\u4e2d\u6587"
        ];

        SessionLaunchRequest request = Valid() with { Arguments = hostile };

        SessionLaunchRequest restored = SessionLaunchProtocol.ReadRequest(
            SessionLaunchProtocol.SerializeRequest(request));

        Assert.Equal(hostile, restored.Arguments);
    }

    [Fact]
    public void EnvironmentAndWorkingDirectorySurviveTheRoundTrip()
    {
        SessionLaunchRequest request = Valid() with
        {
            WorkingDirectory = @"E:\repo\some project",
            Environment = new Dictionary<string, string>
            {
                ["OPENCLAW_SUPERVISOR_MODE"] = "external",
                ["LITERAL"] = "%NOT_EXPANDED%"
            }
        };

        SessionLaunchRequest restored = SessionLaunchProtocol.ReadRequest(
            SessionLaunchProtocol.SerializeRequest(request));

        Assert.Equal(@"E:\repo\some project", restored.WorkingDirectory);
        Assert.Equal("%NOT_EXPANDED%", restored.Environment!["LITERAL"]);
    }

    // The guest composes PATH and NODE_OPTIONS from these named values, so a
    // serialization gap would silently strip the native redirect rather than
    // fail loudly.
    [Fact]
    public void TheNamedGuestComposedValuesSurviveTheRoundTrip()
    {
        SessionLaunchRequest request = Valid() with
        {
            PathPrefix = @"C:\agent\node",
            NodeOptionsSuffix = "--import file:///C:/agent/native-redirect.mjs",
            NativeRootPath = @"C:\agent\agent-native\content",
        };

        SessionLaunchRequest restored = SessionLaunchProtocol.ReadRequest(
            SessionLaunchProtocol.SerializeRequest(request));

        Assert.Equal(@"C:\agent\node", restored.PathPrefix);
        Assert.Equal("--import file:///C:/agent/native-redirect.mjs", restored.NodeOptionsSuffix);
        Assert.Equal(@"C:\agent\agent-native\content", restored.NativeRootPath);
    }

    [Fact]
    public void AnUnsupportedSchemaVersionIsRejectedRatherThanInterpreted()
    {
        // The helper and the launcher ship together, so a version mismatch
        // means a stale file or a mixed installation, never something to
        // guess at.
        string json = SessionLaunchProtocol.SerializeRequest(
            Valid() with { SchemaVersion = SessionLaunchProtocol.CurrentSchemaVersion + 1 });

        SessionLaunchException exception = Assert.Throws<SessionLaunchException>(
            () => SessionLaunchProtocol.ReadRequest(json));

        Assert.Contains("schema version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingWorkingDirectoryIsRejected()
    {
        // Execution does not inherit the caller's directory, so an absent
        // value would silently become the system directory.
        string json = SessionLaunchProtocol.SerializeRequest(
            Valid() with { WorkingDirectory = null });

        SessionLaunchException exception = Assert.Throws<SessionLaunchException>(
            () => SessionLaunchProtocol.ReadRequest(json));

        Assert.Contains("working directory", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("requestId")]
    [InlineData("executable")]
    [InlineData("arguments")]
    public void AnIncompleteRequestIsRejected(string omitted)
    {
        SessionLaunchRequest request = omitted switch
        {
            "requestId" => Valid() with { RequestId = null },
            "executable" => Valid() with { Executable = null },
            _ => Valid() with { Arguments = null }
        };

        Assert.Throws<SessionLaunchException>(
            () => SessionLaunchProtocol.ReadRequest(
                SessionLaunchProtocol.SerializeRequest(request)));
    }

    [Fact]
    public void MalformedJsonIsRejected()
    {
        Assert.Throws<SessionLaunchException>(
            () => SessionLaunchProtocol.ReadRequest("{ not json"));
    }

    [Fact]
    public void AnEmptyArgumentVectorIsAllowed()
    {
        // `openclaw` with no arguments is upstream-owned behavior, so it must
        // reach the application rather than being rejected here.
        SessionLaunchRequest restored = SessionLaunchProtocol.ReadRequest(
            SessionLaunchProtocol.SerializeRequest(Valid() with { Arguments = [] }));

        Assert.Empty(restored.Arguments!);
    }

    [Fact]
    public void ResultRoundTripsAndDistinguishesAFailedLaunch()
    {
        SessionLaunchResult failed = SessionLaunchProtocol.ReadResult(
            SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
            {
                RequestId = "r-1",
                Launched = false,
                Error = "node.exe is missing"
            }));

        Assert.False(failed.Launched);
        Assert.Null(failed.ExitCode);
        Assert.Equal("node.exe is missing", failed.Error);

        SessionLaunchResult succeeded = SessionLaunchProtocol.ReadResult(
            SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
            {
                RequestId = "r-1",
                Launched = true,
                ExitCode = 64
            }));

        // 64 is also the helper's own failure code; only `launched` can tell
        // these apart, which is why the control result exists.
        Assert.True(succeeded.Launched);
        Assert.Equal(64, succeeded.ExitCode);
    }

    [Fact]
    public void AFailedResultMayCarryNoRequestIdButALaunchedOneMayNot()
    {
        // The helper cannot know the request id when the request itself was
        // unreadable, which is precisely when the control result matters most.
        SessionLaunchResult anonymousFailure = SessionLaunchProtocol.ReadResult(
            SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
            {
                Launched = false,
                Error = "the request is not valid JSON"
            }));

        Assert.False(anonymousFailure.Launched);
        Assert.Null(anonymousFailure.RequestId);

        Assert.Throws<SessionLaunchException>(
            () => SessionLaunchProtocol.ReadResult(
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    Launched = true,
                    ExitCode = 0
                })));
    }

    [Fact]
    public void TheResultPathIsDerivedFromTheRequestPath()
    {
        Assert.Equal(
            @"C:\ws\req-1.json.result.json",
            SessionLaunchProtocol.ResultPathFor(@"C:\ws\req-1.json"));
    }
}
