using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class DiagnosticsRedactorTests
{
    [Theory]
    [InlineData("apiKey")]
    [InlineData("apikey")]
    [InlineData("access_token")]
    [InlineData("clientSecret")]
    [InlineData("Password")]
    [InlineData("credential")]
    public void CredentialShapedMembersLoseTheirValue(string member)
    {
        string redacted = DiagnosticsRedactor.Redact(
            $$"""{"{{member}}": "ghu_verysecretvalue"}""");

        Assert.DoesNotContain("ghu_verysecretvalue", redacted, StringComparison.Ordinal);
        Assert.Contains(DiagnosticsRedactor.Placeholder, redacted, StringComparison.Ordinal);
        Assert.Contains(member, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryConfigurationSurvivesUnchanged()
    {
        const string json = """{"gateway": {"port": 18789}, "logLevel": "debug"}""";

        Assert.Equal(json, DiagnosticsRedactor.Redact(json));
    }

    [Fact]
    public void EscapedQuotesInsideASecretDoNotEndTheRedaction()
    {
        string redacted = DiagnosticsRedactor.Redact(
            """{"token": "ab\"cd", "port": 1}""");

        Assert.DoesNotContain("ab", redacted, StringComparison.Ordinal);
        Assert.Contains("\"port\": 1", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCredentialMemberInADocumentIsRedacted()
    {
        string redacted = DiagnosticsRedactor.Redact(
            """{"a": {"apiKey": "one"}, "b": {"apiKey": "two"}}""");

        Assert.DoesNotContain("one", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("two", redacted, StringComparison.Ordinal);
    }
}
