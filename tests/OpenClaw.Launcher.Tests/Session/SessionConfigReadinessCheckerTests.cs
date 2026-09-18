using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionConfigReadinessCheckerTests : IDisposable
{
    private readonly string _profile = TestDirectory.Create();

    private string ConfigPath =>
        Path.Combine(_profile, ".openclaw", "openclaw.json");

    public void Dispose()
    {
        Directory.Delete(_profile, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void WriteConfig(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, content);
    }

    private static void AssertResult(
        SessionConfigReadinessResult result,
        SessionConfigReadinessState state,
        SessionConfigReadinessReason reason)
    {
        Assert.Equal(state, result.State);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ADeletedOrNeverCreatedConfigIsAbsent()
    {
        SessionConfigReadinessResult missing = SessionConfigReadinessChecker.Classify(
            "r1",
            ConfigPath,
            File.ReadAllText,
            File.Exists);
        SessionConfigReadinessResult raced = SessionConfigReadinessChecker.Classify(
            "r2",
            ConfigPath,
            _ => throw new FileNotFoundException(),
            _ => true);

        AssertResult(
            missing,
            SessionConfigReadinessState.Absent,
            SessionConfigReadinessReason.ConfigFileMissing);
        AssertResult(
            raced,
            SessionConfigReadinessState.Absent,
            SessionConfigReadinessReason.ConfigFileMissing);
    }

    [Fact]
    public void AnUnreadableConfigIsNotReady()
    {
        SessionConfigReadinessResult result = SessionConfigReadinessChecker.Classify(
            "r1",
            ConfigPath,
            _ => throw new UnauthorizedAccessException(),
            _ => true);

        AssertResult(
            result,
            SessionConfigReadinessState.NotReady,
            SessionConfigReadinessReason.ConfigFileUnreadable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ invalid")]
    public void AnInvalidConfigIsNotReady(string content)
    {
        WriteConfig(content);

        SessionConfigReadinessResult result = SessionConfigReadinessChecker.Classify(
            "r1",
            ConfigPath,
            File.ReadAllText,
            File.Exists);

        AssertResult(
            result,
            SessionConfigReadinessState.NotReady,
            SessionConfigReadinessReason.ConfigFileInvalid);
    }

    [Theory]
    [InlineData("{}", SessionConfigReadinessReason.GatewayMissing)]
    [InlineData("""{"gateway":{}}""", SessionConfigReadinessReason.GatewayModeMissing)]
    [InlineData("""{"gateway":{"mode":42}}""", SessionConfigReadinessReason.GatewayModeMissing)]
    [InlineData("""{"gateway":{"mode":"remote"}}""", SessionConfigReadinessReason.GatewayModeNotLocal)]
    [InlineData("""{"gateway":{"mode":"LOCAL"}}""", SessionConfigReadinessReason.GatewayModeNotLocal)]
    public void AConfigWithoutExactLocalModeIsNotReady(
        string content,
        SessionConfigReadinessReason reason)
    {
        WriteConfig(content);

        SessionConfigReadinessResult result = SessionConfigReadinessChecker.Classify(
            "r1",
            ConfigPath,
            File.ReadAllText,
            File.Exists);

        AssertResult(result, SessionConfigReadinessState.NotReady, reason);
    }

    [Fact]
    public void AGeneratedConfigWithLocalModeIsStartupEligible()
    {
        WriteConfig(
            """
            {
              // Generated configuration can contain comments.
              "gateway": {
                "mode": "local",
              },
            }
            """);

        SessionConfigReadinessResult result = SessionConfigReadinessChecker.Classify(
            "r1",
            ConfigPath,
            File.ReadAllText,
            File.Exists);

        AssertResult(
            result,
            SessionConfigReadinessState.StartupEligible,
            SessionConfigReadinessReason.GatewayModeLocal);
    }

    [Fact]
    public void RunWritesAProtocolFailureForAMalformedRequest()
    {
        string requestPath = Path.Combine(_profile, "request.json");
        string? resultText = null;

        int exitCode = SessionConfigReadinessChecker.Run(
            requestPath,
            _ => "{ invalid",
            (_, value) => resultText = value,
            _profile);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.NotNull(resultText);
        Assert.Contains("\"error\":", resultText, StringComparison.Ordinal);
    }
}
