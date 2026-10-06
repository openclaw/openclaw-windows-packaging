using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionCompanionConfigTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private string ConfigPath => Path.Combine(_root, "agent", ".openclaw", "openclaw.json");
    private string RequestPath => Path.Combine(_root, "request.json");
    private string ResultPath => SessionLaunchProtocol.ResultPathFor(RequestPath);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SessionCompanionConfigRequest Request(int port = 19001) => new()
    {
        RequestId = "companion-1",
        Port = port,
        NodePath = @"C:\agent\node.exe",
        ApplicationDirectory = @"C:\package\app",
        Environment = new Dictionary<string, string>()
    };

    private int Run(
        SessionCompanionConfigRequest request,
        Func<SessionCompanionConfigRequest, string, int> applyPatch,
        Func<(int ExitCode, string Output)>? readEffectiveConfiguration = null)
    {
        File.WriteAllText(RequestPath, SessionCompanionConfigProtocol.SerializeRequest(request));
        return SessionCompanionConfig.Run(
            RequestPath, File.ReadAllText, File.WriteAllText,
            profileRoot: Path.Combine(_root, "agent"),
            applyPatch: applyPatch,
            readEffectiveConfiguration: _ =>
            {
                (int exit, string output) = readEffectiveConfiguration?.Invoke() ?? ReadEffectiveConfiguration();
                if (exit != 0)
                {
                    return (exit, output);
                }
                using JsonDocument gateway = JsonDocument.Parse(output);
                return (0, JsonSerializer.Serialize(new CompanionConfigSnapshot(
                    gateway.RootElement, ConfigPath,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : ""))),
                    File.Exists(ConfigPath)), CompanionConfigPatchContext.Default.CompanionConfigSnapshot));
            });
    }

    private int RunWithoutPatch(
        SessionCompanionConfigRequest request,
        Func<(int ExitCode, string Output)>? readEffectiveConfiguration = null)
    {
        bool patchAttempted = false;
        int exit = Run(request, (_, _) =>
        {
            patchAttempted = true;
            return 17;
        }, readEffectiveConfiguration);
        Assert.False(patchAttempted, "This operation must not attempt a configuration write.");
        return exit;
    }

    private (int ExitCode, string Output) ReadEffectiveConfiguration()
    {
        if (!File.Exists(ConfigPath))
        {
            return (0, "{}");
        }

        JsonObject config = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        return config["gateway"] is JsonNode gateway
            ? (0, gateway.ToJsonString())
            : (0, "{}");
    }

    private int ApplyPatch(SessionCompanionConfigRequest request, string patchPath)
    {
        Assert.StartsWith(Path.Combine(_root, "agent", ".openclaw"), patchPath, StringComparison.Ordinal);
        JsonObject patch = JsonNode.Parse(File.ReadAllText(patchPath))!.AsObject();
        JsonObject config = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject()
            : new JsonObject();
        JsonObject gateway = config["gateway"] as JsonObject ?? new JsonObject();
        JsonObject auth = gateway["auth"] as JsonObject ?? new JsonObject();
        foreach ((string key, JsonNode? value) in patch["gateway"]!["auth"]!.AsObject())
        {
            auth[key] = value?.DeepClone();
        }
        foreach ((string key, JsonNode? value) in patch["gateway"]!.AsObject()
            .Where(pair => pair.Key != "auth"))
        {
            gateway[key] = value?.DeepClone();
        }
        gateway["auth"] = auth;
        config["gateway"] = gateway;
        File.WriteAllText(ConfigPath, config.ToJsonString());
        return 0;
    }

    [Fact]
    public void MissingAgentConfigUsesUpstreamPatchInsideAgentProfile()
    {
        int exitCode = Run(Request(), ApplyPatch);

        Assert.Equal(0, exitCode);
        SessionCompanionConfigResult result = SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1");
        Assert.Equal(19001, result.Port);
        Assert.Equal(64, result.Token!.Length);
        using JsonDocument persisted = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.Equal(result.Token, persisted.RootElement.GetProperty("gateway")
            .GetProperty("auth").GetProperty("token").GetString());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, ".companion-config-*"));
    }

    [Fact]
    public void MissingAgentConfigRestoresCompanionsSavedToken()
    {
        int exitCode = Run(Request() with { Token = "saved-companion-token" }, ApplyPatch);

        Assert.Equal(0, exitCode);
        SessionCompanionConfigResult result = SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1");
        Assert.Equal("saved-companion-token", result.Token);
        using JsonDocument persisted = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.Equal("saved-companion-token", persisted.RootElement.GetProperty("gateway")
            .GetProperty("auth").GetProperty("token").GetString());
    }

    [Fact]
    public void ExistingAgentConfigPreservesPortTokenAndUnrelatedSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, """
            {"models":{"custom":"preserved"},"gateway":{
              "mode":"local","port":20123,"bind":"loopback",
              "auth":{"mode":"token","token":"keep-existing","extra":"preserve"},
              "nodes":{"commands":{"allow":["system.run"]}}
            }}
            """);
        int exitCode = Run(Request(), ApplyPatch);

        Assert.Equal(0, exitCode);
        SessionCompanionConfigResult result = SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1");
        Assert.Equal(20123, result.Port);
        Assert.Equal("keep-existing", result.Token);
        JsonObject config = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        Assert.Equal("preserved", config["models"]!["custom"]!.GetValue<string>());
        Assert.Equal("preserve", config["gateway"]!["auth"]!["extra"]!.GetValue<string>());
        Assert.Equal("system.run", config["gateway"]!["nodes"]!["commands"]!["allow"]![0]!.GetValue<string>());
    }

    [Fact]
    public void ExistingJson5IncludedGatewaySurvivesRepeatedPrepareAndCheckWithoutChangingConfigBytes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        const string config = """
            {
              // Keep this comment and the upstream-owned include intact.
              gateway: { $include: "./gateway.json5" },
            }
            """;
        File.WriteAllText(ConfigPath, config);

        Func<(int ExitCode, string Output)> readEffectiveConfiguration = () => (0, """
            {"mode":"local","port":20123,"bind":"loopback",
            "auth":{"mode":"token","token":"included-token"}}
            """);
        int firstPrepareExitCode = RunWithoutPatch(Request(), readEffectiveConfiguration);
        int repeatedPrepareExitCode = RunWithoutPatch(Request(), readEffectiveConfiguration);
        int checkExitCode = RunWithoutPatch(Request(port: 0) with { CheckOnly = true }, readEffectiveConfiguration);

        Assert.Equal(0, firstPrepareExitCode);
        Assert.Equal(0, repeatedPrepareExitCode);
        Assert.Equal(0, checkExitCode);
        SessionCompanionConfigResult result = SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1");
        Assert.Equal(20123, result.Port);
        Assert.Equal("included-token", result.Token);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void RedactedEffectiveGatewayTokenFailsWithoutPatchingOrChangingConfigBytes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        const string config = """
            {
              gateway: { mode: "local", bind: "loopback", port: 20123,
              auth: { mode: "token", token: "literal-token" } },
            }
            """;
        File.WriteAllText(ConfigPath, config);

        int exitCode = RunWithoutPatch(
            Request(),
            () => (0, """
                {"mode":"local","port":20123,"bind":"loopback",
                "auth":{"mode":"token","token":"__OPENCLAW_REDACTED__"}}
                """));

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        SessionCompanionConfigResult result = SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1");
        Assert.Contains("did not expose", result.Error, StringComparison.Ordinal);
        Assert.Null(result.Token);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void CheckOnlyReturnsEffectiveConfigWithoutChangingItsBytesOrPatching()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        const string config = """
            {"gateway":{"mode":"local","port":20123,"bind":"loopback",
            "auth":{"mode":"token","token":"existing-token"}}}
            """;
        File.WriteAllText(ConfigPath, config);

        int exitCode = RunWithoutPatch(Request(port: 0) with { CheckOnly = true });

        Assert.Equal(0, exitCode);
        SessionCompanionConfigResult result = SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1");
        Assert.Equal(20123, result.Port);
        Assert.Equal("existing-token", result.Token);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void CheckOnlyMissingConfigFailsWithoutPatching()
    {
        int exitCode = RunWithoutPatch(Request(port: 0) with { CheckOnly = true });

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.Contains("missing", SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1").Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData("""{"gateway":""")]
    [InlineData("""{"gateway":{"mode":"remote","port":19001,"auth":{"token":"token"}}}""")]
    public void CheckOnlyRejectsMalformedOrUnsupportedConfigWithoutPatching(string config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, config);

        int exitCode = RunWithoutPatch(Request(port: 0) with { CheckOnly = true });

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.NotNull(SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1").Error);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData("""{"gateway":{"mode":"remote"}}""")]
    [InlineData("""{"gateway":{"port":19001,"auth":{"mode":"token","token":"existing-token"}}}""")]
    [InlineData("""{"gateway":{"mode":"local","bind":"lan"}}""")]
    [InlineData("""{"gateway":{"mode":"local","auth":{"mode":"password"}}}""")]
    [InlineData("""{"gateway":{"mode":"local","auth":{"token":""}}}""")]
    [InlineData("""{"gateway":{"mode":"local","auth":{"password":"keep-password"}}}""")]
    [InlineData("""{"gateway":{"mode":"local","port":19001,"auth":{"password":"keep-password","token":"not-used"}}}""")]
    [InlineData("""{"gateway":{"mode":"local","port":19001,"remote":{"password":"fallback-password"}}}""")]
    [InlineData("""{"gateway":{"mode":"local","port":19001,"auth":{"token":"not-used"},"remote":{"password":"fallback-password"}}}""")]
    public void ExistingIncompatibleConfigFailsBeforeAnyWrite(string config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, config);
        int exitCode = RunWithoutPatch(Request());

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.NotNull(SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1").Error);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void UpstreamPatchFailureDoesNotCreateSuccessShapedResult()
    {
        int exitCode = Run(Request(), (_, _) => 17);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        SessionCompanionConfigResult result = SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1");
        Assert.Contains("exit code 17", result.Error, StringComparison.Ordinal);
        Assert.Null(result.Token);
        Assert.False(File.Exists(ConfigPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, ".companion-config-*"));
    }

    [Fact]
    public void UnsupportedContractAndMismatchedResultAreRejected()
    {
        Assert.Throws<SessionLaunchException>(() => SessionCompanionConfigProtocol.ReadRequest(
            SessionCompanionConfigProtocol.SerializeRequest(Request() with
            {
                SchemaVersion = SessionLaunchProtocol.CurrentSchemaVersion + 1
            })));
        Assert.Throws<SessionLaunchException>(() => SessionCompanionConfigProtocol.ReadResult(
            SessionCompanionConfigProtocol.SerializeResult(new SessionCompanionConfigResult
            {
                RequestId = "someone-else",
                Port = 19001,
                Token = "token"
            }), "companion-1"));
        Assert.Throws<SessionLaunchException>(() => SessionCompanionConfigProtocol.ReadResult(
            SessionCompanionConfigProtocol.SerializeResult(new SessionCompanionConfigResult
            {
                RequestId = "companion-1",
                Error = ""
            }), "companion-1"));
    }

    [Theory]
    [InlineData("""{"models":{"custom":"preserved"}}""")]
    [InlineData("""{"models":{"custom":"preserved"},"gateway":{}}""")]
    public void ExistingConfigWithoutGatewayCanBePreparedButNotChecked(string config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, config);

        int checkExit = RunWithoutPatch(Request(port: 0) with { CheckOnly = true });

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, checkExit);
        Assert.Equal(config, File.ReadAllText(ConfigPath));

        Assert.Equal(0, Run(Request(), ApplyPatch));
        JsonObject prepared = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        Assert.Equal("preserved", prepared["models"]!["custom"]!.GetValue<string>());
        Assert.Equal("local", prepared["gateway"]!["mode"]!.GetValue<string>());
        Assert.Equal(0, RunWithoutPatch(Request(port: 0) with { CheckOnly = true }));
    }

    [Fact]
    public void ExplicitTokenModeDoesNotInferPasswordAuthentication()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        const string config = """
            {"gateway":{"mode":"local","port":19001,
              "auth":{"mode":"token","token":"effective-token","password":"unused-password"},
              "remote":{"password":"unused-remote-password"}}}
            """;
        File.WriteAllText(ConfigPath, config);

        Assert.Equal(0, RunWithoutPatch(Request()));
        Assert.Equal("effective-token", SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(ResultPath), "companion-1").Token);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void GuestHelperDispatchesCompanionRequestsAndRejectsUnsupportedSchema()
    {
        File.WriteAllText(RequestPath, SessionCompanionConfigProtocol.SerializeRequest(
            Request() with { SchemaVersion = SessionLaunchProtocol.CurrentSchemaVersion + 1 }));
        using var error = new StringWriter();

        int exitCode = OpenClaw.SessionHost.Program.Run(
            ["--companion-config", RequestPath],
            new SessionProcessLauncher(),
            error,
            File.ReadAllText,
            (_, _) => throw new InvalidOperationException("Companion must not launch an application."));

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(ResultPath));
        Assert.Contains("schema version", result.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

}
