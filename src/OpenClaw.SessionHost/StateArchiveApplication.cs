using System.Text.Json;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

internal sealed record StateArchiveApplicationResult(
    string ArchivePath,
    IReadOnlyList<StateTransferAsset> Assets,
    IReadOnlyList<string> Warnings,
    string? SourceProfile = null);

internal interface IStateArchiveApplication
{
    StateArchiveApplicationResult Capture(string profile, string output, bool dryRun);
    StateArchiveApplicationResult Extract(string profile, string archive, string destination);
    IReadOnlyList<string> RequiredSources(string profile, string previousProfile);
    void RebaseConfiguration(string profile, string previousProfile, string currentProfile);
}

internal sealed class StateArchiveApplication : IStateArchiveApplication
{
    private readonly SessionStateTransferRequest _request;

    public StateArchiveApplication(SessionStateTransferRequest request)
    {
        _request = request;
    }

    public StateArchiveApplicationResult Capture(string profile, string output, bool dryRun)
    {
        List<string> arguments = ["backup", "create", "--json", "--output", output];
        arguments.Add(dryRun ? "--dry-run" : "--verify");
        using JsonDocument document = Run(profile, arguments);
        JsonElement root = document.RootElement;
        string archivePath = ReadString(root, "archivePath");
        if (!dryRun &&
            (!root.TryGetProperty("verified", out JsonElement verified) ||
                verified.ValueKind != JsonValueKind.True))
        {
            throw new SessionLaunchException("Upstream did not verify the captured backup.");
        }
        return new StateArchiveApplicationResult(
            archivePath,
            ReadAssets(root),
            ReadWarnings(root));
    }

    public StateArchiveApplicationResult Extract(
        string profile,
        string archive,
        string destination)
    {
        using JsonDocument document = Run(
            profile,
            ["backup", "restore", archive, "--target", destination, "--json"]);
        JsonElement root = document.RootElement;
        string archiveRoot = ReadString(root, "archiveRoot");
        string manifestPath = Path.Combine(destination, archiveRoot, "manifest.json");
        using FileStream manifest = TrustedPath.OpenRead(destination, manifestPath, protectContents: true);
        using JsonDocument metadata = JsonDocument.Parse(manifest);
        return new StateArchiveApplicationResult(
            archive,
            ReadAssets(metadata.RootElement),
            ReadWarnings(root),
            ReadSourceProfile(metadata.RootElement));
    }

    public IReadOnlyList<string> RequiredSources(string profile, string previousProfile)
    {
        using JsonDocument document = ConfigurationOperation(profile, previousProfile, profile, "inspect");
        return [.. document.RootElement.GetProperty("requiredSources").EnumerateArray()
            .Select(value => value.GetString() ??
                throw new SessionLaunchException("A required configuration dependency has no path."))];
    }

    public void RebaseConfiguration(
        string profile,
        string previousProfile,
        string currentProfile)
    {
        using JsonDocument result = ConfigurationOperation(profile, previousProfile, currentProfile, "write");
    }

    private JsonDocument ConfigurationOperation(
        string profile,
        string previousProfile,
        string currentProfile,
        string mode)
    {
        const string expression = """
            import { resolve, relative, isAbsolute, join, dirname, sep } from "node:path";
            import { readFile, writeFile } from "node:fs/promises";
            import { existsSync } from "node:fs";
            import { createRequire } from "node:module";
            import { pathToFileURL } from "node:url";
            class StateOperationError extends Error {}
            try {
            const [app, physicalHome, previousHome, currentHome, mode] = process.argv.slice(1);
            const expectedPath = join(physicalHome, ".openclaw", "openclaw.json");
            const JSON5 = createRequire(join(app, "package.json"))("json5");
            const sdk = (name) => pathToFileURL(resolve(app, "dist", "plugin-sdk", `${name}.js`)).href;
            const required = new Set();
            const documents = new Map();
            const suffix = (home, value) => {
              const result = relative(home, value);
              if (isAbsolute(result) || result === ".." || result.startsWith(`..${sep}`))
                throw new StateOperationError("external-dependency");
              return result;
            };
            const sourcePath = (value, base = previousHome) => {
              if (typeof value !== "string" || !value.trim() || value.includes("${"))
                throw new StateOperationError("unresolved-dependency");
              let absolute = value === "~" ? previousHome :
                value.startsWith("~/") || value.startsWith("~\\") ? join(previousHome, value.slice(2)) :
                resolve(base, value);
              if (isAbsolute(value)) {
                const inPhysical = relative(physicalHome, absolute);
                if (!isAbsolute(inPhysical) && inPhysical !== ".." && !inPhysical.startsWith(`..${sep}`))
                  absolute = join(previousHome, inPhysical);
              }
              suffix(previousHome, absolute);
              required.add(absolute);
              return absolute;
            };
            const physical = (absolute) => join(physicalHome, suffix(previousHome, absolute));
            const rebase = (value, base = previousHome) => {
              const absolute = sourcePath(value, base);
              return isAbsolute(value) ? join(currentHome, suffix(previousHome, absolute)) : value;
            };
            const property = (object, key, base) => {
              if (object?.[key] !== undefined) {
                const value = object[key];
                if (typeof value !== "string" || (!isAbsolute(value) &&
                    value !== "~" && !value.startsWith("~/") && !value.startsWith("~\\")))
                  throw new StateOperationError("unresolved-dependency");
                object[key] = rebase(value, base);
              }
            };
            const paths = (object, key) => {
              if (object?.[key] !== undefined) {
                if (!Array.isArray(object[key])) throw new StateOperationError("invalid-configuration");
                object[key] = object[key].map((value) => {
                  const holder = { value };
                  property(holder, "value");
                  return holder.value;
                });
              }
            };
            const scalarPaths = new Set([
              "agents.defaults.workspace", "agents.list.*.workspace", "agents.list.*.agentDir",
              "plugins.installs.*.installPath", "plugins.installs.*.sourcePath",
            ]);
            const arrayPaths = new Set(["skills.load.extraDirs", "plugins.load.paths"]);
            const visit = async (draft, base, scope = "") => {
              if (!draft || typeof draft !== "object") return;
              if ("$include" in draft) {
                const includes = Array.isArray(draft.$include) ? draft.$include : [draft.$include];
                for (const include of includes) await load(sourcePath(include, base), scope);
                draft.$include = Array.isArray(draft.$include)
                  ? includes.map((value) => rebase(value, base)) : rebase(draft.$include, base);
              }
              for (const [key, value] of Object.entries(draft)) {
                if (key === "$include") continue;
                const childScope = scope === "plugins.installs" ? `${scope}.*` :
                  scope ? `${scope}.${key}` : key;
                if (scalarPaths.has(childScope)) property(draft, key);
                if (arrayPaths.has(childScope)) paths(draft, key);
                if (Array.isArray(value)) {
                  for (const child of value) if (child && typeof child === "object")
                    await visit(child, base, `${childScope}.*`);
                } else if (value && typeof value === "object") await visit(value, base, childScope);
              }
            };
            const load = async (absolute, scope = "") => {
              if (documents.has(absolute)) {
                if (documents.get(absolute).scope !== scope)
                  throw new StateOperationError("invalid-configuration");
                return;
              }
              if (!existsSync(physical(absolute))) return;
              if (documents.size >= 10000) throw new StateOperationError("invalid-configuration");
              const draft = JSON5.parse(await readFile(physical(absolute), "utf8"));
              documents.set(absolute, { draft, scope });
              await visit(draft, dirname(absolute), scope);
            };
            const originalConfig = join(previousHome, ".openclaw", "openclaw.json");
            await load(originalConfig);
            const missing = () => [...required].some((path) => !existsSync(physical(path)));
            const readSnapshot = async () => {
              const { readConfigFileSnapshot } = await import(sdk("health"));
              const snapshot = await readConfigFileSnapshot({
                observe: false, recoverSuspicious: false, pluginValidation: "core-only",
              });
              if (!snapshot.valid || resolve(snapshot.path).toLowerCase() !== resolve(expectedPath).toLowerCase())
                throw new StateOperationError("invalid-configuration");
              return snapshot;
            };
            const writeDocuments = async () => {
              for (const [path, { draft }] of documents)
                await writeFile(physical(path), JSON.stringify(draft, null, 2) + "\n");
            };
            if (mode === "inspect" && !missing()) {
              await writeDocuments();
              const snapshot = await readSnapshot();
              await visit(structuredClone(snapshot.config), join(previousHome, ".openclaw"));
            }
            if (mode === "write") {
              if (missing()) throw new StateOperationError("missing-dependency");
              const mutate = async () => {
                const { mutateConfigFile } = await import(sdk("config-mutation"));
                await mutateConfigFile({
                  base: "source", writeOptions: { expectedConfigPath: expectedPath },
                  afterWrite: { mode: "none", reason: "The package owns state activation." },
                  async mutate(draft) { await visit(draft, join(previousHome, ".openclaw")); },
                });
              };
              if (currentHome !== physicalHome && existsSync(expectedPath)) await mutate();
              // Included files must be relocated before the upstream loader can resolve them.
              // All writes are in task-owned staging, never the original account's profile.
              await writeDocuments();
              if (currentHome === physicalHome) {
                const snapshot = await readSnapshot();
                if (snapshot.exists) await mutate();
              }
            }
            process.stdout.write(JSON.stringify({ requiredSources: [...required] }));
            } catch (error) {
              process.stdout.write(JSON.stringify({
                errorCode: error instanceof StateOperationError ? error.message : "invalid-configuration",
              }));
              process.exitCode = 64;
            }
            """;
        return Run(
            profile,
            ["--input-type=module", "--eval", expression,
                _request.ApplicationDirectory!, profile, previousProfile, currentProfile,
                mode],
            nodeExpression: true);
    }

    private JsonDocument Run(
        string profile,
        IReadOnlyList<string> arguments,
        bool nodeExpression = false)
    {
        foreach (string name in new[] { "OPENCLAW_HOME", "OPENCLAW_STATE_DIR", "OPENCLAW_CONFIG_PATH", "OPENCLAW_PROFILE" })
        {
            string? effective = _request.Environment?.TryGetValue(name, out string? value) == true
                ? value : Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(effective))
            {
                throw new SessionLaunchException(
                    $"The agent has a {name} override. Remove it before using packaged state transfer.");
            }
        }
        List<string> nodeArguments = [];
        if (_request.NativePreloadPath is { Length: > 0 } preload)
        {
            nodeArguments.AddRange(["--import", new Uri(preload).AbsoluteUri]);
        }
        if (!nodeExpression)
        {
            nodeArguments.Add(Path.Combine(_request.ApplicationDirectory!, "openclaw.mjs"));
        }
        nodeArguments.AddRange(arguments);
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in _request.Environment ?? new Dictionary<string, string>())
        {
            environment.Add(name, value);
        }
        environment["OPENCLAW_HOME"] = profile;
        environment["OPENCLAW_STATE_DIR"] = Path.Combine(profile, ".openclaw");
        environment["OPENCLAW_CONFIG_PATH"] = Path.Combine(profile, ".openclaw", "openclaw.json");
        SessionProcessOutput output = SessionProcessLauncher.Capture(new SessionLaunchRequest
        {
            RequestId = _request.RequestId,
            Executable = _request.NodePath,
            Arguments = nodeArguments,
            WorkingDirectory = profile,
            Environment = environment,
            NativeRootPath = _request.NativeRootPath,
            NodeOptionsSuffix = _request.NodeOptionsSuffix
        });
        if (output.ExitCode != 0)
        {
            if (nodeExpression)
            {
                string reason = ReadConfigurationFailure(output.StandardOutput);
                throw new SessionLaunchException(reason +
                    " No source or live state was changed. Use `--dry-run` to inspect scope, or `clawctl collect-logs`.");
            }
            throw new SessionLaunchException(
                $"The bundled OpenClaw state operation failed (exit {output.ExitCode}). " +
                "Confirm the package supports backup create/verify/restore and run `clawctl collect-logs`.");
        }
        try
        {
            return JsonDocument.Parse(output.StandardOutput);
        }
        catch (JsonException)
        {
            throw new SessionLaunchException(
                "The bundled OpenClaw state operation did not return its supported JSON contract.");
        }
    }

    private static string ReadConfigurationFailure(string output)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("errorCode", out JsonElement code))
            {
                return code.GetString() switch
                {
                    "external-dependency" => "Recovery requires a configuration, workspace, plugin, skill, or agent path outside the profile.",
                    "unresolved-dependency" => "A configured path is relative, environment-dependent, or otherwise unresolvable for automatic recovery.",
                    "missing-dependency" => "Recovery is missing a required profile-contained dependency.",
                    _ => "The recovery configuration is invalid, unreadable, or unsupported by the bundled SDK."
                };
            }
        }
        catch (JsonException)
        {
            return "The bundled configuration SDK did not return its supported recovery contract.";
        }
        return "The bundled configuration SDK did not return its supported recovery contract.";
    }

    private static string ReadSourceProfile(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("paths", out JsonElement paths) || paths.ValueKind != JsonValueKind.Object)
        {
            throw new SessionLaunchException("The archive does not identify its source state directory.");
        }
        string state = ReadString(paths, "stateDir");
        if (!Path.IsPathFullyQualified(state) ||
            !Path.GetFileName(state).Equals(".openclaw", StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionLaunchException(
                "Automatic activation requires a full archive of a profile-contained .openclaw directory.");
        }
        return Path.GetDirectoryName(state)!;
    }
    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: > 0 } value)
        {
            throw new SessionLaunchException($"Upstream state metadata has no '{name}'.");
        }
        return value;
    }

    private static List<StateTransferAsset> ReadAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            throw new SessionLaunchException("Upstream state metadata has no asset inventory.");
        }
        List<StateTransferAsset> result = [];
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                throw new SessionLaunchException("Upstream returned a malformed state asset inventory.");
            }
            result.Add(new StateTransferAsset(
                ReadString(asset, "kind"),
                ReadString(asset, "sourcePath"),
                ReadString(asset, "archivePath")));
        }
        return result;
    }

    private static List<string> ReadWarnings(JsonElement root)
    {
        List<string> warnings = [];
        if (root.TryGetProperty("warnings", out JsonElement values) &&
            values.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { } warning)
                {
                    warnings.Add(warning);
                }
            }
        }
        if (root.TryGetProperty("sqliteInventoryVerified", out JsonElement inventory) &&
            inventory.ValueKind == JsonValueKind.False)
        {
            warnings.Add("This legacy archive cannot prove complete SQLite inventory coverage.");
        }
        return warnings;
    }
}
