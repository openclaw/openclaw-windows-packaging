using System.Text.Json.Nodes;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionCompanionConfigNodeTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private string Profile => Path.Combine(_root, "agent");
    private string ConfigPath => Path.Combine(Profile, ".openclaw", "openclaw.json");
    private string Application => Path.Combine(_root, "app");

    // Exercise the production Node adapter, faking only its upstream SDK boundary.
    private const string SdkFixture = """
        import { existsSync, readFileSync, writeFileSync } from "node:fs";
        import { resolve, dirname } from "node:path";
        import { createHash } from "node:crypto";
        const root = process.env.COMPANION_TEST_ROOT;
        const defaultPath = resolve(root, "agent", ".openclaw", "openclaw.json");
        function readSnapshot() {
          const envPath = resolve(root, "environment.json");
          if (existsSync(envPath)) Object.assign(process.env, JSON.parse(readFileSync(envPath, "utf8")));
          const selection = resolve(root, "selection.txt");
          const path = existsSync(selection) ? readFileSync(selection, "utf8") : defaultPath;
          const exists = existsSync(path);
          const raw = exists ? readFileSync(path, "utf8") : "";
          const config = raw ? JSON.parse(raw) : {};
          if (config.gateway?.$include) {
            config.gateway = JSON.parse(readFileSync(resolve(dirname(path), config.gateway.$include), "utf8"));
          }
          return {
            path, exists, valid: true, config,
            hash: createHash("sha256").update(exists ? raw : "missing-file").digest("hex"),
          };
        }
        export function loadConfig() {
          writeFileSync(defaultPath, readFileSync(defaultPath + ".bak"));
          throw new Error("Recovering loadConfig must never be used by Companion.");
        }
        export async function readConfigFileSnapshot(options = {}) {
          if (options.observe !== false) {
            writeFileSync(resolve(root, "config-health.json"), "{}");
          }
          if (options.recoverSuspicious !== false) {
            writeFileSync(defaultPath, readFileSync(defaultPath + ".bak"));
          }
          if (options.pluginValidation !== "core-only") {
            writeFileSync(resolve(root, "plugin-state.sqlite-shm"), "observed");
          }
          return readSnapshot();
        }
        export async function readConfigFileSnapshotForWrite() {
          writeFileSync(resolve(root, "config-health.json"), "{}");
          return { snapshot: readSnapshot(), writeOptions: {} };
        }
        export async function mutateConfigFile(options) {
          const replacement = resolve(root, "concurrent.json");
          if (existsSync(replacement)) {
            const rootConfig = JSON.parse(readFileSync(defaultPath, "utf8"));
            const target = rootConfig.gateway?.$include
              ? resolve(dirname(defaultPath), rootConfig.gateway.$include) : defaultPath;
            writeFileSync(target, readFileSync(replacement));
          }
          const snapshot = readSnapshot();
          if (options.baseHash !== snapshot.hash ||
              options.writeOptions.expectedConfigPath !== snapshot.path) {
            throw new Error("Config mutation conflict.");
          }
          if (options.afterWrite.mode !== "none" || options.base !== "source") {
            throw new Error("The package must retain lifecycle ownership and source settings.");
          }
          const draft = structuredClone(snapshot.config);
          await options.mutate(draft, { snapshot });
          writeFileSync(snapshot.path, JSON.stringify(draft));
        }
        """;

    public SessionCompanionConfigNodeTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        string sdk = Path.Combine(Application, "dist", "plugin-sdk");
        Directory.CreateDirectory(sdk);
        File.WriteAllText(Path.Combine(Application, "package.json"), """{"type":"module"}""");
        foreach (string module in new[] { "health", "config-mutation", "config-runtime" })
        {
            File.WriteAllText(Path.Combine(sdk, module + ".js"), SdkFixture);
        }
        using IDisposable state = new SessionStateAccess(Path.Combine(_root, "state-access")).EnterReader();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (int ExitCode, SessionCompanionConfigResult Result) Run(bool check = false, string? overrideName = null)
    {
        string? node = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "node.exe"))
            .FirstOrDefault(File.Exists);
        Assert.True(node is not null, "These adapter tests require Node.js on PATH.");
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Environment.GetEnvironmentVariables().Keys)
        {
            if (name.StartsWith("OPENCLAW_", StringComparison.OrdinalIgnoreCase))
            {
                environment[name] = "";
            }
        }
        environment["COMPANION_TEST_ROOT"] = _root;
        environment["NODE_OPTIONS"] = "";
        environment["HOME"] = Profile;
        environment["USERPROFILE"] = Profile;
        if (overrideName is not null)
        {
            environment[overrideName] = "override";
        }
        string requestPath = Path.Combine(_root, "request.json");
        File.WriteAllText(requestPath, SessionCompanionConfigProtocol.SerializeRequest(new SessionCompanionConfigRequest
        {
            RequestId = "node-contract",
            Port = 19001,
            CheckOnly = check,
            ApplicationDirectory = Application,
            NodePath = node,
            Environment = environment
        }));

        int exit = SessionCompanionConfig.Run(requestPath, File.ReadAllText, File.WriteAllText,
            profileRoot: Profile,
            enterState: new SessionStateAccess(Path.Combine(_root, "state-access")).EnterReader);
        return (exit, SessionCompanionConfigProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(requestPath)), "node-contract"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionAdapterInitializesMissingGatewayAndRechecksIt(bool configExists)
    {
        if (configExists)
        {
            File.WriteAllText(ConfigPath, """{"unrelated":{"keep":true}}""");
        }
        else
        {
            Directory.Delete(Path.GetDirectoryName(ConfigPath)!);
        }
        (int exit, SessionCompanionConfigResult prepared) = Run();
        Assert.Equal(0, exit);
        Assert.Equal(19001, prepared.Port);
        Assert.Equal(64, prepared.Token!.Length);
        string config = File.ReadAllText(ConfigPath);
        if (configExists)
        {
            Assert.True(JsonNode.Parse(config)!["unrelated"]!["keep"]!.GetValue<bool>());
        }

        (int checkedExit, SessionCompanionConfigResult checkedResult) = Run(check: true);

        Assert.Equal(0, checkedExit);
        Assert.Equal(prepared.Token, checkedResult.Token);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, ".companion-config-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckAndNoOpPrepareNeitherRecoverNorWriteObservations(bool check)
    {
        const string config = """{"gateway":{"mode":"local","port":19001,"auth":{"token":"current"}}}""";
        const string backup = """{"gateway":{"mode":"local","port":20123,"auth":{"token":"backup"}}}""";
        File.WriteAllText(ConfigPath, config);
        File.WriteAllText(ConfigPath + ".bak", backup);
        string[] before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);

        (int exit, SessionCompanionConfigResult result) = Run(check);

        Assert.Equal(0, exit);
        Assert.Equal("current", result.Token);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
        Assert.Equal(backup, File.ReadAllText(ConfigPath + ".bak"));
        string requestPath = Path.Combine(_root, "request.json");
        Assert.Equal(before.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => path != requestPath && path != SessionLaunchProtocol.ResultPathFor(requestPath)).Order());
    }

    public static IEnumerable<object[]> OverrideCases()
    {
        foreach (string name in new[]
        {
            "OPENCLAW_GATEWAY_PORT", "OPENCLAW_GATEWAY_TOKEN", "OPENCLAW_GATEWAY_PASSWORD",
            "OPENCLAW_HOME", "OPENCLAW_PROFILE", "OPENCLAW_CONFIG_PATH", "OPENCLAW_STATE_DIR",
            "OPENCLAW_GATEWAY_URL"
        })
        {
            yield return [name, false];
            yield return [name, true];
        }
    }

    [Theory]
    [MemberData(nameof(OverrideCases))]
    public void AmbientAndUpstreamLoadedOverridesAreRejectedBeforeMutation(string name, bool loadedByUpstream)
    {
        const string config = """{"gateway":{"mode":"local","port":19001}}""";
        File.WriteAllText(ConfigPath, config);
        if (loadedByUpstream)
        {
            File.WriteAllText(Path.Combine(_root, "environment.json"), new JsonObject { [name] = "override" }.ToJsonString());
        }

        (int exit, SessionCompanionConfigResult result) = Run(overrideName: loadedByUpstream ? null : name);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exit);
        Assert.Null(result.Token);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentRootOrIncludedGatewayChangeIsNotOverwritten(bool included)
    {
        string includedPath = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "gateway.json");
        string initial = included
            ? """{"gateway":{"$include":"gateway.json"}}"""
            : """{"gateway":{"mode":"local","port":19001}}""";
        File.WriteAllText(ConfigPath, initial);
        File.WriteAllText(includedPath, """{"mode":"local","port":19001}""");
        const string replacementGateway = """{"mode":"local","port":20123,"auth":{"token":"newer"}}""";
        string replacement = included ? replacementGateway : "{\"gateway\":" + replacementGateway + "}";
        File.WriteAllText(Path.Combine(_root, "concurrent.json"), replacement);

        (int exit, SessionCompanionConfigResult result) = Run();

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exit);
        Assert.Null(result.Token);
        Assert.Equal(replacement, File.ReadAllText(included ? includedPath : ConfigPath));
        if (included)
        {
            Assert.Equal(initial, File.ReadAllText(ConfigPath));
        }
    }

    [Fact]
    public void UpstreamSelectingAnotherConfigCannotOverwriteItsToken()
    {
        string foreignPath = Path.Combine(_root, "foreign.json");
        const string foreign = """{"gateway":{"mode":"local","port":20123,"auth":{"token":"foreign"}}}""";
        File.WriteAllText(foreignPath, foreign);
        File.WriteAllText(Path.Combine(_root, "selection.txt"), foreignPath);

        (int exit, SessionCompanionConfigResult result) = Run();

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exit);
        Assert.Null(result.Token);
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal(foreign, File.ReadAllText(foreignPath));
    }
}
