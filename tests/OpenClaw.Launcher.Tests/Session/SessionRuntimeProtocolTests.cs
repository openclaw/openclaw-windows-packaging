using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// The contract for installing the agent's own Node.js runtime.
/// </summary>
public sealed class SessionRuntimeProtocolTests
{
    private static SessionRuntimeInstallRequest Valid() => new()
    {
        RequestId = "r1",
        ArchivePath = @"C:\Package\runtime\node-v24.15.0-win-x64.zip"
    };

    [Fact]
    public void AValidRequestRoundTrips()
    {
        SessionRuntimeInstallRequest parsed = SessionRuntimeProtocol.ReadRequest(
            SessionRuntimeProtocol.SerializeRequest(Valid()));

        Assert.Equal("r1", parsed.RequestId);
        Assert.Equal(Valid().ArchivePath, parsed.ArchivePath);
        Assert.True(parsed.UpdateUserPath);
    }

    // The host names the archive; a relative or traversing value would let a
    // malformed request point the guest outside the package.
    [Theory]
    [InlineData("")]
    [InlineData(@"runtime\node.zip")]
    [InlineData(@"C:\Package\..\elsewhere\node.zip")]
    public void AnArchivePathThatIsNotFullyQualifiedIsRejected(string archivePath)
    {
        Assert.Throws<SessionLaunchException>(
            () => SessionRuntimeProtocol.ReadRequest(
                SessionRuntimeProtocol.SerializeRequest(
                    Valid() with { ArchivePath = archivePath })));
    }

    [Fact]
    public void ARequestWithoutAnIdIsRejected()
    {
        Assert.Throws<SessionLaunchException>(
            () => SessionRuntimeProtocol.ReadRequest(
                SessionRuntimeProtocol.SerializeRequest(Valid() with { RequestId = null })));
    }

    // The helper and the launcher ship together, so a mismatch means a stale
    // file or a mixed installation.
    [Fact]
    public void AMismatchedSchemaVersionIsRejected()
    {
        Assert.Throws<SessionLaunchException>(
            () => SessionRuntimeProtocol.ReadRequest(
                SessionRuntimeProtocol.SerializeRequest(Valid() with
                {
                    SchemaVersion = SessionLaunchProtocol.CurrentSchemaVersion + 1
                })));
    }

    // A failed install has to arrive as a readable result, because the host
    // reports it rather than guessing from an exit code.
    [Fact]
    public void AFailureResultRoundTrips()
    {
        SessionRuntimeInstallResult parsed = SessionRuntimeProtocol.ReadResult(
            SessionRuntimeProtocol.SerializeResult(new SessionRuntimeInstallResult
            {
                RequestId = "r1",
                Error = "the archive is corrupt"
            }));

        Assert.Equal("the archive is corrupt", parsed.Error);
        Assert.Null(parsed.ExecutablePath);
    }
}

