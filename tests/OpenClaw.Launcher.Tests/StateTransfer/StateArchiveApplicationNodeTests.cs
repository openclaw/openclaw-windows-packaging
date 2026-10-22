using System.Text.Json.Nodes;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;
using HostTrustedPath = OpenClaw.Launcher.Session.TrustedPath;

namespace OpenClaw.Launcher.Tests.StateTransfer;

public sealed class StateArchiveApplicationNodeTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private string Original => Path.Combine(_root, "A1-B2");
    private string Staged => Path.Combine(_root, "shadow");
    private string Target => Path.Combine(_root, "C3-D4");
    private string Application => Path.Combine(_root, "app");
    private string Config => Path.Combine(Staged, ".openclaw", "openclaw.json");
    private string Exchange => Path.Combine(_root, "exchange");

    private const string SdkFixture = """
        import { readFileSync, writeFileSync, existsSync } from "node:fs";
        import { resolve, dirname } from "node:path";
        const path = process.env.OPENCLAW_CONFIG_PATH;
        const expand = (value, base) => {
          if (!value || typeof value !== "object") return value;
          let result = value;
          if (value.$include) {
            const includes = Array.isArray(value.$include) ? value.$include : [value.$include];
            result = Object.assign({}, ...includes.map((name) =>
              expand(JSON.parse(readFileSync(resolve(base, name), "utf8")), dirname(resolve(base, name)))));
            Object.assign(result, value);
            delete result.$include;
          }
          for (const [key, child] of Object.entries(result)) {
            if (Array.isArray(child)) result[key] = child.map((entry) => expand(entry, base));
            else if (child && typeof child === "object") result[key] = expand(child, base);
          }
          return result;
        };
        const snapshot = () => {
          const exists = existsSync(path);
          let source = exists ? JSON.parse(readFileSync(path, "utf8")) : {};
          let config = expand(structuredClone(source), dirname(path));
          const effective = process.env.STATE_TEST_EFFECTIVE;
          if (effective && existsSync(effective)) config = JSON.parse(readFileSync(effective, "utf8"));
          return { exists, path, source, config, valid: true };
        };
        export async function readConfigFileSnapshot(options) {
          if (options.observe !== false || options.recoverSuspicious !== false ||
              options.pluginValidation !== "core-only")
            throw new Error("Unsafe read options containing fixture-secret.");
          return snapshot();
        }
        export async function mutateConfigFile(options) {
          if (options.base !== "source" || options.afterWrite.mode !== "none" ||
              options.writeOptions.expectedConfigPath !== path)
            throw new Error("Unsafe mutation options containing fixture-secret.");
          const current = snapshot();
          const draft = structuredClone(current.source);
          await options.mutate(draft, { snapshot: current });
          writeFileSync(path, JSON.stringify(draft));
          writeFileSync(process.env.STATE_TEST_MUTATION, "upstream mutation completed");
        }
        """;

    private const string CliFixture = """
        import { readFileSync, writeFileSync } from "node:fs";
        import { join } from "node:path";
        const args = process.argv.slice(2);
        const control = JSON.parse(readFileSync(join(process.env.OPENCLAW_HOME, "contract.json"), "utf8"));
        if (control.fail) {
          process.stderr.write("fixture-secret credentials");
          process.exitCode = 23;
        } else {
          const archivePath = args[args.indexOf("--output") + 1];
          if (control.scratch) {
            writeFileSync(join(process.env.TEMP, "scratch.txt"), "synthetic scratch");
            writeFileSync(join(process.env.OPENCLAW_HOME, "scratch-path.txt"), process.env.TEMP);
          }
          if (!args.includes("--dry-run")) writeFileSync(archivePath, "synthetic archive");
          process.stdout.write(JSON.stringify({
            archivePath, verified: control.verified,
            assets: [{ kind: "state", sourcePath: join(process.env.OPENCLAW_HOME, ".openclaw"),
              archivePath: "snapshot/payload/state" }],
          }));
        }
        """;

    public StateArchiveApplicationNodeTests()
    {
        using var parent = HostTrustedPath.TryOpenValidatedDirectory(_root, expectedIdentity: null) ??
            throw new InvalidOperationException("The fixture root is unavailable.");
        using var exchange = HostTrustedPath.CreatePrivateDirectory(parent, Exchange);
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        Directory.CreateDirectory(Path.Combine(Staged, "Work"));
        string sdk = Path.Combine(Application, "dist", "plugin-sdk");
        Directory.CreateDirectory(sdk);
        File.WriteAllText(Path.Combine(Application, "package.json"), """{"type":"module"}""");
        File.WriteAllText(Path.Combine(Application, "openclaw.mjs"), CliFixture);
        File.WriteAllText(Path.Combine(sdk, "health.js"), SdkFixture);
        File.WriteAllText(Path.Combine(sdk, "config-mutation.js"), SdkFixture);
        string json5 = Path.Combine(Application, "node_modules", "json5");
        Directory.CreateDirectory(json5);
        File.WriteAllText(Path.Combine(json5, "package.json"), """{"main":"index.cjs"}""");
        File.WriteAllText(Path.Combine(json5, "index.cjs"), "module.exports = { parse: JSON.parse };");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private StateArchiveApplication Create(string? overrideName = null, string? preload = null)
    {
        string? node = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "node.exe"))
            .FirstOrDefault(File.Exists);
        Assert.NotNull(node);
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENCLAW_HOME"] = "",
            ["OPENCLAW_STATE_DIR"] = "",
            ["OPENCLAW_CONFIG_PATH"] = "",
            ["OPENCLAW_PROFILE"] = "",
            ["NODE_OPTIONS"] = "",
            ["HOME"] = Staged,
            ["USERPROFILE"] = Staged,
            ["STATE_TEST_MUTATION"] = Path.Combine(_root, "mutation.txt"),
            ["STATE_TEST_EFFECTIVE"] = Path.Combine(_root, "effective.json"),
            ["STATE_TEST_PRELOAD"] = Path.Combine(_root, "preload-ran.txt")
        };
        if (overrideName is not null)
        {
            environment[overrideName] = "another selection";
        }
        return new StateArchiveApplication(new SessionStateTransferRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            WorkspaceDirectory = Exchange,
            NodePath = node,
            ApplicationDirectory = Application,
            NativePreloadPath = preload,
            Environment = environment
        });
    }

    [Fact]
    public void CaptureKeepsRuntimeScratchInsideThePrivateExchange()
    {
        File.WriteAllText(Path.Combine(Staged, "contract.json"), """{"verified":true,"scratch":true}""");

        _ = Create().Capture(Staged, Path.Combine(Exchange, "capture.tar.gz"), dryRun: false);

        string scratch = File.ReadAllText(Path.Combine(Staged, "scratch-path.txt"));
        Assert.StartsWith(Exchange + Path.DirectorySeparatorChar, scratch, StringComparison.Ordinal);

        // Locate the file by enumeration so the reported path is only compared,
        // never reused as a file-system argument.
        string scratchFile = Assert.Single(
            Directory.EnumerateFiles(Exchange, "scratch.txt", SearchOption.AllDirectories));
        Assert.Equal(scratch, Path.GetDirectoryName(scratchFile));
        Assert.Equal("synthetic scratch", File.ReadAllText(scratchFile));
        StateArchiveStoreTests.AssertPrivateFile(scratchFile, protectedAcl: false);
    }

    private void WriteConfiguration(string workspace)
    {
        JsonObject config = new()
        {
            ["agents"] = new JsonObject { ["defaults"] = new JsonObject { ["workspace"] = workspace } },
            ["gateway"] = new JsonObject { ["mode"] = "local" },
            ["unrelated"] = new JsonObject
            {
                ["workspace"] = Original + "\\looks-like-a-path-but-is-not-a-config-field",
                ["credential"] = "fixture-secret"
            }
        };
        File.WriteAllText(Config, config.ToJsonString());
    }

    [Fact]
    public void CaptureLoadsTheNativePreloadFromASpacedWindowsPath()
    {
        string preload = WritePreload();
        File.WriteAllText(Path.Combine(Staged, "contract.json"), """{"verified":true}""");
        string output = Path.Combine(_root, "capture.tar.gz");

        StateArchiveApplicationResult result = Create(preload: preload).Capture(Staged, output, dryRun: false);

        Assert.Equal(output, result.ArchivePath);
        Assert.Equal("synthetic archive", File.ReadAllText(output));
        Assert.Equal("loaded", File.ReadAllText(Path.Combine(_root, "preload-ran.txt")));
    }

    [Fact]
    public void ConfigurationInspectionLoadsTheSameNativePreloadBeforeTheSdk()
    {
        string preload = WritePreload();
        WriteConfiguration(Path.Combine(Original, "Work"));

        IReadOnlyList<string> sources = Create(preload: preload).RequiredSources(Staged, Original);

        Assert.Contains(Path.Combine(Original, "Work"), sources);
        Assert.Equal("loaded", File.ReadAllText(Path.Combine(_root, "preload-ran.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "mutation.txt")));
    }

    private string WritePreload()
    {
        string directory = Path.Combine(_root, "preload scripts");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "redirect [test].mjs");
        File.WriteAllText(path, """
            import { writeFileSync } from "node:fs";
            writeFileSync(process.env.STATE_TEST_PRELOAD, "loaded");
            """);
        return path;
    }

    [Fact]
    public void ProductionAdapterRelocatesOnlyOwnedFieldsThroughTheUpstreamSdk()
    {
        WriteConfiguration(Path.Combine(Original, "Work"));
        StateArchiveApplication application = Create();

        IReadOnlyList<string> sources = application.RequiredSources(Staged, Original);
        application.RebaseConfiguration(Staged, Original, Staged);
        application.RebaseConfiguration(Staged, Staged, Target);

        Assert.Contains(Path.Combine(Original, "Work"), sources);
        JsonNode config = JsonNode.Parse(File.ReadAllText(Config))!;
        Assert.Equal(Path.Combine(Target, "Work"), config["agents"]!["defaults"]!["workspace"]!.GetValue<string>());
        Assert.Equal(Original + "\\looks-like-a-path-but-is-not-a-config-field",
            config["unrelated"]!["workspace"]!.GetValue<string>());
        Assert.Equal("fixture-secret", config["unrelated"]!["credential"]!.GetValue<string>());
        Assert.Equal("upstream mutation completed", File.ReadAllText(Path.Combine(_root, "mutation.txt")));
        Assert.False(Directory.Exists(Original));
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void NestedIncludesRetainTheirScopeAndProfileContainedDependencies()
    {
        string include = Path.Combine(Staged, ".openclaw", "agents.json");
        File.WriteAllText(Config, """{"agents":{"$include":"agents.json"},"gateway":{"mode":"local"}}""");
        File.WriteAllText(include, new JsonObject
        {
            ["defaults"] = new JsonObject { ["workspace"] = Path.Combine(Original, "Work") }
        }.ToJsonString());
        StateArchiveApplication application = Create();

        IReadOnlyList<string> sources = application.RequiredSources(Staged, Original);
        application.RebaseConfiguration(Staged, Original, Staged);
        application.RebaseConfiguration(Staged, Staged, Target);

        Assert.Contains(Path.Combine(Original, ".openclaw", "agents.json"), sources);
        Assert.Contains(Path.Combine(Original, "Work"), sources);
        Assert.Equal(Path.Combine(Target, "Work"),
            JsonNode.Parse(File.ReadAllText(include))!["defaults"]!["workspace"]!.GetValue<string>());
        Assert.Equal("agents.json",
            JsonNode.Parse(File.ReadAllText(Config))!["agents"]!["$include"]!.GetValue<string>());
    }

    [Fact]
    public void RegisteredCustomAgentDatabaseFollowsTheProfileThroughBothRebases()
    {
        string customDatabase = Path.Combine(Staged, "CustomAgent", "agent.sqlite");
        string registry = Path.Combine(Staged, ".openclaw", "state", "openclaw.sqlite");
        string external = Path.Combine(_root, "elsewhere", "external.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(customDatabase)!);
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(Config, new JsonObject
        {
            ["gateway"] = new JsonObject { ["mode"] = "local" },
            ["agents"] = new JsonObject
            {
                ["list"] = new JsonArray(new JsonObject
                {
                    ["id"] = "custom",
                    ["agentDir"] = Path.Combine(Original, "CustomAgent")
                })
            }
        }.ToJsonString());
        RunSqlite(customDatabase, """
            CREATE TABLE sessions (id TEXT PRIMARY KEY, note TEXT NOT NULL) STRICT;
            INSERT INTO sessions VALUES ('s1', 'custom-agent-row');
            """);
        RunNode("""
            import { DatabaseSync } from "node:sqlite";
            const database = new DatabaseSync(process.argv[1]);
            database.exec(`CREATE TABLE agent_databases (
              agent_id TEXT NOT NULL, path TEXT NOT NULL, schema_version INTEGER NOT NULL,
              last_seen_at INTEGER NOT NULL, size_bytes INTEGER, PRIMARY KEY (agent_id, path)) STRICT`);
            const insert = database.prepare("INSERT INTO agent_databases VALUES (?, ?, 1, ?, 0)");
            insert.run("main", "agents/main/agent/openclaw-agent.sqlite", 1);
            insert.run("custom", process.argv[2], 2);
            insert.run("external", process.argv[3], 3);
            database.close();
            """, registry, Path.Combine(Original, "CustomAgent", "agent.sqlite"), external);
        StateArchiveApplication application = Create();

        application.RebaseConfiguration(Staged, Original, Staged);

        Assert.Equal(
            [
                "custom|" + customDatabase,
                "external|" + external,
                "main|agents/main/agent/openclaw-agent.sqlite"
            ],
            ReadRegistry(registry));
        // The registered locator now resolves to a real, readable custom-agent database.
        Assert.Equal("custom-agent-row", ReadSingle(customDatabase, "SELECT note FROM sessions"));

        application.RebaseConfiguration(Staged, Staged, Target);

        Assert.Equal(
            [
                "custom|" + Path.Combine(Target, "CustomAgent", "agent.sqlite"),
                "external|" + external,
                "main|agents/main/agent/openclaw-agent.sqlite"
            ],
            ReadRegistry(registry));
        Assert.False(Directory.Exists(Original));
    }

    [Fact]
    public void ARegistryWithoutAgentDatabasesIsLeftUntouched()
    {
        string registry = Path.Combine(Staged, ".openclaw", "state", "openclaw.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        RunSqlite(registry, "CREATE TABLE unrelated (value TEXT NOT NULL) STRICT; INSERT INTO unrelated VALUES ('kept');");
        WriteConfiguration(Path.Combine(Original, "Work"));

        Create().RebaseConfiguration(Staged, Original, Staged);

        Assert.Equal("kept", ReadSingle(registry, "SELECT value FROM unrelated"));
    }

    [Fact]
    public void AnUnreadableRegistryBlocksWithAnActionableMessage()
    {
        string registry = Path.Combine(Staged, ".openclaw", "state", "openclaw.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(registry, "this is not a sqlite database, and is longer than the header check");
        WriteConfiguration(Path.Combine(Original, "Work"));

        SessionLaunchException failure = Assert.Throws<SessionLaunchException>(() =>
            Create().RebaseConfiguration(Staged, Original, Staged));

        Assert.Contains("agent database registry", failure.Message, StringComparison.Ordinal);
        Assert.Contains("No source or live state was changed", failure.Message, StringComparison.Ordinal);
    }

    private static string FindNode() => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory.Trim('"'), "node.exe"))
        .First(File.Exists);

    private static string RunNode(string script, params string[] arguments)
    {
        System.Diagnostics.ProcessStartInfo start = new(FindNode())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("--eval");
        start.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "node did not exit");
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.TrimEnd();
    }

    private static void RunSqlite(string database, string sql) => RunNode("""
        import { DatabaseSync } from "node:sqlite";
        const database = new DatabaseSync(process.argv[1]);
        database.exec(process.argv[2]);
        database.close();
        """, database, sql);

    private static string ReadSingle(string database, string query) => RunNode("""
        import { DatabaseSync } from "node:sqlite";
        const database = new DatabaseSync(process.argv[1], { readOnly: true });
        console.log(Object.values(database.prepare(process.argv[2]).get())[0]);
        """, database, query);

    private static string[] ReadRegistry(string registry) => RunNode("""
        import { DatabaseSync } from "node:sqlite";
        const database = new DatabaseSync(process.argv[1], { readOnly: true });
        for (const row of database.prepare("SELECT agent_id, path FROM agent_databases ORDER BY agent_id").all())
          console.log(`${row.agent_id}|${row.path}`);
        """, registry).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void EffectiveSdkConfigurationAlsoContributesRequiredDependencies()
    {
        WriteConfiguration(Path.Combine(Original, "Work"));
        string effective = Path.Combine(_root, "effective.json");
        File.WriteAllText(effective, new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["list"] = new JsonArray(new JsonObject
                {
                    ["id"] = "custom",
                    ["agentDir"] = Path.Combine(Original, "CustomAgent")
                })
            }
        }.ToJsonString());

        IReadOnlyList<string> required = Create().RequiredSources(Staged, Original);

        Assert.Contains(Path.Combine(Original, "CustomAgent"), required);
    }

    [Theory]
    [InlineData("external")]
    [InlineData("relative")]
    [InlineData("environment")]
    public void UnsupportedDependenciesBlockWithoutLeakingConfiguration(string kind)
    {
        string workspace = kind switch
        {
            "external" => Path.Combine(_root, "outside", "workspace"),
            "relative" => "workspace",
            _ => "${PRIVATE_WORKSPACE}\\workspace"
        };
        WriteConfiguration(workspace);
        string before = File.ReadAllText(Config);

        SessionLaunchException failure = Assert.Throws<SessionLaunchException>(() =>
            Create().RequiredSources(Staged, Original));

        Assert.DoesNotContain("fixture-secret", failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(Config));
        Assert.False(File.Exists(Path.Combine(_root, "mutation.txt")));
        Assert.Contains(kind == "external" ? "outside the profile" : "unresolvable",
            failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OPENCLAW_HOME")]
    [InlineData("OPENCLAW_STATE_DIR")]
    [InlineData("OPENCLAW_CONFIG_PATH")]
    [InlineData("OPENCLAW_PROFILE")]
    public void AgentSelectionOverridesCannotSilentlyChangeTheCapturedState(string name)
    {
        WriteConfiguration(Path.Combine(Original, "Work"));
        SessionLaunchException failure = Assert.Throws<SessionLaunchException>(() =>
            Create(name).RequiredSources(Staged, Original));
        Assert.Contains(name, failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "mutation.txt")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CaptureRequiresVerificationAndNeverIncludesUpstreamStderr(bool verified, bool fail)
    {
        File.WriteAllText(Path.Combine(Staged, "contract.json"),
            new JsonObject { ["verified"] = verified, ["fail"] = fail }.ToJsonString());
        StateArchiveApplication application = Create();
        string output = Path.Combine(_root, "capture.tar.gz");

        if (fail)
        {
            SessionLaunchException error = Assert.Throws<SessionLaunchException>(() =>
                application.Capture(Staged, output, dryRun: false));
            Assert.Contains("exit 23", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-secret", error.Message, StringComparison.Ordinal);
        }
        else
        {
            StateArchiveApplicationResult result = application.Capture(Staged, output, dryRun: false);
            Assert.Equal(output, result.ArchivePath);
            Assert.Equal(Path.Combine(Staged, ".openclaw"), Assert.Single(result.Assets).SourcePath);
            Assert.Equal("synthetic archive", File.ReadAllText(output));
        }
    }

    [Fact]
    public void AnUnverifiedUpstreamArchiveCannotBePresentedAsVerified()
    {
        File.WriteAllText(Path.Combine(Staged, "contract.json"), """{"verified":false}""");
        Assert.Throws<SessionLaunchException>(() =>
            Create().Capture(Staged, Path.Combine(_root, "unverified.tar.gz"), dryRun: false));
    }
}
