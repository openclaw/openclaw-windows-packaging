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

    // Gateway launch logs are free-form output, not JSON, and a Control UI
    // link carries its credential in the URL rather than in a member.
    [Theory]
    [InlineData("Control UI: http://127.0.0.1:18789/#token=ghu_verysecretvalue")]
    [InlineData("GET https://example.test/api?api_key=ghu_verysecretvalue&page=2")]
    [InlineData("connecting to wss://127.0.0.1:18789/ws?access_token=ghu_verysecretvalue")]
    [InlineData("OPENAI_API_KEY=ghu_verysecretvalue node openclaw.mjs")]
    [InlineData("Authorization: Bearer ghu_verysecretvalue")]
    [InlineData("""{"headers": {"Authorization": "Bearer ghu_verysecretvalue"}}""")]
    public void CredentialsInLogTextLoseTheirValue(string line)
    {
        string redacted = DiagnosticsRedactor.Redact(line);

        Assert.DoesNotContain("ghu_verysecretvalue", redacted, StringComparison.Ordinal);
        Assert.Contains(DiagnosticsRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    // A credential's length says nothing about its sensitivity: this is u:p.
    [Fact]
    public void AShortAuthorizationCredentialIsStillRedacted()
    {
        Assert.Equal(
            $"Authorization: Basic {DiagnosticsRedactor.Placeholder}",
            DiagnosticsRedactor.Redact("Authorization: Basic dTpw"));
    }

    [Theory]
    [InlineData("Control UI: http://127.0.0.1:18789/?view=chat#section")]
    [InlineData("Basic authentication is enabled.")]
    [InlineData("Use the bearer certificate provided by Windows.")]
    [InlineData("Set the api key= option in settings.")]
    [InlineData("Gateway auth token was missing. Generated a runtime token for this startup.")]
    public void OrdinaryLogTextSurvivesUnchanged(string line)
    {
        Assert.Equal(line, DiagnosticsRedactor.Redact(line));
    }

    // Guest-written text reaches every pattern. Under a backtracking engine
    // each of these lines takes time quadratic in its length, so a megabyte of
    // them would stall collection for minutes; here they must finish at once.
    [Fact(Timeout = 30_000)]
    public async Task CraftedTextIsRedactedInLinearTime()
    {
        string crafted = string.Join(
            '\n',
            "?" + string.Concat(Enumerable.Repeat("key", 120_000)),
            "\"" + string.Concat(Enumerable.Repeat("key", 120_000)),
            string.Concat(Enumerable.Repeat("KEY", 120_000)));

        string redacted = await Task.Run(() => DiagnosticsRedactor.Redact(crafted));

        Assert.Equal(crafted, redacted);
    }
}
