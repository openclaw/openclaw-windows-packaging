import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const args = process.argv.slice(2);
assert.equal(
  process.env.XDG_CACHE_HOME,
  path.join(process.env.OPENCLAW_STATE_DIR, "cache"),
  "Expected an isolated plugin snapshot cache.",
);
fs.mkdirSync(process.env.XDG_CACHE_HOME, { recursive: true });
fs.writeFileSync(path.join(process.env.XDG_CACHE_HOME, "fixture-cache"), "owned");

if (args[0] === "--version") {
  console.log(JSON.parse(fs.readFileSync("package.json", "utf8")).version);
} else if (args[0] === "completion") {
  assert.deepEqual(args, ["completion", "--shell", "powershell"]);
  console.log("Register-ArgumentCompleter -Native -CommandName openclaw -ScriptBlock {}");
} else {
  assert.deepEqual(args, [
    "plugins", "inspect", "gateway-isolation", "--runtime", "--json",
  ], "Payload validation must inspect defaults, never enable a plugin.");
  const configPath = process.env.OPENCLAW_CONFIG_PATH;
  const config = fs.existsSync(configPath)
    ? JSON.parse(fs.readFileSync(configPath, "utf8"))
    : {};
  const entry = config.plugins?.entries?.["gateway-isolation"];
  assert.notEqual(entry?.enabled, true, "Do not inject explicit enablement.");
  assert.equal(config.plugins?.allow, undefined, "Do not inject an allowlist.");
  const pluginRoot = path.resolve("dist/extensions/gateway-isolation");
  const manifest = JSON.parse(
    fs.readFileSync(path.join(pluginRoot, "openclaw.plugin.json"), "utf8"),
  );
  const enabled = manifest.enabledByDefault === true && entry?.enabled !== false;
  if (process.env.OPENCLAW_FIXTURE_FAIL_RUNTIME === "1") {
    throw new Error("Requested runtime inspection fixture failure.");
  }
  const routes = [];
  const descriptors = [];
  if (enabled) {
    const { default: plugin } = await import(
      pathToFileURL(path.join(pluginRoot, "index.js")).href
    );
    plugin.register({
      session: {
        controls: {
          registerControlUiDescriptor(descriptor) { descriptors.push(descriptor); },
        },
      },
      registerHttpRoute(route) { routes.push(route); },
    });
    assert.equal(descriptors.length, 1);
    assert.equal(descriptors[0].label, "Windows Launcher");
    assert.deepEqual(descriptors[0].requiredScopes, ["operator.read"]);
    assert.equal(routes.length, 1);
    assert.equal(routes[0].auth, "gateway");
    assert.equal(routes[0].match, "exact");
    assert.equal(routes[0].path, "/plugins/gateway-isolation/status");
  }
  console.log(JSON.stringify({
    plugin: {
      id: manifest.id,
      origin: "bundled",
      enabled,
      explicitlyEnabled: false,
      activationSource: enabled ? "default" : "disabled",
      activated: enabled,
      status: enabled ? "loaded" : "disabled",
      imported: enabled,
      httpRoutes: routes.length,
    },
    httpRouteCount: routes.length,
    gatewayMethods: [],
    tools: [],
    services: [],
    diagnostics: [],
  }));
}
