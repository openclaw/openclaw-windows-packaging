import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import fs from "node:fs/promises";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createGatewayIsolationPlugin } from "../../plugins/gateway-isolation/index.js";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
assert.equal(process.platform, "win32", "The real-runtime lane requires Windows.");
assert.ok(process.argv[2], "Pass the prepared OpenClaw payload directory.");
const payloadRoot = path.resolve(process.argv[2]);
const application = path.join(payloadRoot, "app");
const metadataPath = path.resolve(payloadRoot, "payload-metadata.json");
if (!metadataPath.startsWith(payloadRoot + path.sep)) {
  throw new Error("Payload metadata must remain inside the prepared payload directory.");
}
const metadataEntry = await fs.lstat(metadataPath);
if (!metadataEntry.isFile()) {
  throw new Error("Payload metadata must be a regular file, not a symbolic link.");
}
const payload = JSON.parse(await fs.readFile(metadataPath, "utf8"));
assert.equal(payload.repository, "https://github.com/openclaw/openclaw");
assert.match(payload.resolvedCommit, /^[0-9a-f]{40}$/);
assert.match(payload.packageVersion, /^\d{4}\.\d+\.\d+(?:-\d+)?$/);
const identity = JSON.parse(await fs.readFile(path.join(application, "dist", "build-info.json"), "utf8"));
assert.equal(identity.commit, payload.resolvedCommit);
assert.equal(identity.version, payload.packageVersion);
console.log(`Runtime: ${identity.version} ${identity.commit}; Node ${process.version}`);
for (const name of ["index.js", "package.json", "openclaw.plugin.json"]) {
  assert.deepEqual(
    await fs.readFile(path.join(application, "dist", "extensions", "gateway-isolation", name)),
    await fs.readFile(path.join(repository, "plugins", "gateway-isolation", name)),
    `The prepared runtime must contain the current plugin: ${name}`,
  );
}
let instructions;
createGatewayIsolationPlugin({ CLAWCTL_GATEWAY_ISOLATION: "enabled" }, "win32").register({
  on(name, handler) {
    assert.equal(name, "before_prompt_build");
    instructions = handler().prependContext;
  },
  session: { controls: { registerControlUiDescriptor() {} } },
  registerHttpRoute() {},
});
assert.ok(instructions);

const root = await fs.mkdtemp(path.join(os.tmpdir(), "openclaw-context-"));
const workspace = path.join(root, "workspace");
const configPath = path.join(root, "openclaw.json");
await fs.mkdir(workspace);
const sentinel = "fixture-only local read succeeded";
const sentinelPath = path.join(workspace, "deliverable.txt");
await fs.writeFile(sentinelPath, sentinel);
const userFiles = {
  "AGENTS.md": "User-owned fixture instructions. Keep this file unchanged.\n",
  "TOOLS.md": "User-owned fixture tool preferences. Keep this file unchanged.\n",
};
for (const [name, text] of Object.entries(userFiles)) {
  await fs.writeFile(path.join(workspace, name), text);
}
let requestCount = 0;
let requestFailure;
let scenario;
const server = http.createServer(async (request, response) => {
  try {
    assert.equal(request.url, "/v1/chat/completions");
    assert.equal(request.headers.authorization, "Bearer fixture-key");
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    requestCount++;
    const summary = !body.tools?.length;
    console.log(`${scenario.name}: ${summary ? "summary" : "agent"} request ${requestCount}`);
    if (!summary) {
      for (const roles of [["system", "developer"], ["user"]]) {
        const rendered = JSON.stringify(body.messages.filter(message => roles.includes(message.role)));
        assert.equal(rendered.split("## Windows agent session").length - 1, scenario.guidance ? 1 : 0,
          `Each tool-bearing request must have one instruction block in ${roles.join("/")} context.`);
        if (scenario.guidance) {
          assert.ok(rendered.includes(JSON.stringify(instructions).slice(1, -1)),
            "The entire instruction block must reach the model, not just its heading.");
        }
      }
      assert.deepEqual(body.tools.map(tool => tool.function.name), ["read"]);
      scenario.agentRequests++;
      if (scenario.overflow && !scenario.overflowSent) {
        scenario.overflowSent = true;
        response.writeHead(400, { "Content-Type": "application/json" });
        response.end(JSON.stringify({
          error: { type: "invalid_request_error", code: "context_length_exceeded",
            message: "maximum context length exceeded (fixture)" },
        }));
        return;
      }
    } else {
      scenario.summaries++;
    }
    const lastUser = body.messages.findLastIndex(message => message.role === "user");
    const toolResult = body.messages.slice(lastUser + 1).find(message => message.role === "tool");
    if (toolResult) {
      assert.ok(JSON.stringify(toolResult).includes(sentinel));
      scenario.toolResults++;
    }
    const delta = summary
      ? { content: "## Goal\nRead the fixture deliverable.\n## Progress\nThe previous fixture read completed.\n## Next Steps\nContinue the current fixture request. No other work or credentials are needed." }
      : toolResult
      ? { content: "Fixture completed." }
      : {
          tool_calls: [{
            index: 0, id: "fixture-read", type: "function",
            function: { name: "read", arguments: JSON.stringify({ path: sentinelPath }) },
          }],
        };
    response.writeHead(200, { "Content-Type": "text/event-stream" });
    const event = (delta, finish_reason = null) => `data: ${JSON.stringify({
      id: "fixture-response", object: "chat.completion.chunk", created: 1,
      model: "fixture-chat", choices: [{ index: 0, delta, finish_reason }],
    })}\n\n`;
    response.write(event({ role: "assistant" }));
    response.write(event(delta));
    response.write(event({}, summary || toolResult ? "stop" : "tool_calls"));
    response.end("data: [DONE]\n\n");
  } catch (error) {
    requestFailure ??= error;
    response.writeHead(500);
    response.end("Fixture assertion failed");
  }
});
server.listen(0, "127.0.0.1");
await once(server, "listening");
const config = {
  agents: {
    defaults: {
      workspace, skipBootstrap: true,
      model: { primary: "fixture/fixture-chat" },
      compaction: {
        keepRecentTokens: 1, memoryFlush: { enabled: false },
      },
    },
  },
  memory: { search: { enabled: false } },
  models: {
    mode: "replace",
    providers: {
      fixture: {
        baseUrl: `http://127.0.0.1:${server.address().port}/v1`,
        apiKey: "fixture-key", api: "openai-completions",
        models: [{
          id: "fixture-chat", name: "Fixture", reasoning: false, input: ["text"],
          contextWindow: 32768, maxTokens: 1024,
          cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
        }],
      },
    },
  },
  plugins: { allow: ["gateway-isolation", "openai"] },
  tools: { allow: ["read"] },
  logging: { file: path.join(root, "runtime.log") },
};
const env = {};
for (const key of ["SystemRoot", "WINDIR", "ComSpec", "PATH", "PATHEXT"]) {
  if (process.env[key]) env[key] = process.env[key];
}
Object.assign(env, {
  HOME: root, USERPROFILE: root, APPDATA: root, LOCALAPPDATA: root,
  TEMP: root, TMP: root, OPENCLAW_HOME: root,
  OPENCLAW_STATE_DIR: path.join(root, "state"), OPENCLAW_CONFIG_PATH: configPath,
  XDG_CACHE_HOME: path.join(root, "cache"), CLAWCTL_GATEWAY_ISOLATION: "enabled",
  OPENCLAW_NO_AUTO_UPDATE: "1", NO_COLOR: "1",
});

async function run(args) {
  const child = spawn(process.execPath, [path.join(application, "openclaw.mjs"), ...args],
    { cwd: workspace, env, stdio: ["ignore", "pipe", "pipe"], timeout: 90_000 });
  let stdout = "";
  let stderr = "";
  child.stdout.on("data", chunk => { stdout += chunk; });
  child.stderr.on("data", chunk => { stderr += chunk; });
  const [code] = await once(child, "close");
  if (requestFailure) throw requestFailure;
  assert.equal(code, 0, `${stdout}\n${stderr}`);
  return JSON.parse(stdout);
}

async function agent(name, {
  overflow = false, summaryExpected = false, guidance = true,
  sessionKey = "agent:main:isolation-context-fixture",
} = {}) {
  scenario = { name, overflow, guidance, agentRequests: 0, summaries: 0, toolResults: 0 };
  const result = await run([
    "agent", "--local", "--agent", "main", "--session-key", sessionKey,
    "--message", `Read the fixture deliverable. Scenario: ${name}.`,
    "--thinking", "off", "--timeout", "60", "--json",
  ]);
  assert.ok(scenario.agentRequests >= 2, JSON.stringify(result));
  assert.equal(scenario.toolResults, 1, "The fixture read must execute after the captured request.");
  assert.deepEqual(result.meta.toolSummary, { calls: 1, tools: ["read"], failures: 0 });
  if (overflow) {
    assert.equal(scenario.overflowSent, true);
    // Retained-turn compaction can finish without a separate summary-model request.
    assert.equal(result.meta.agentMeta.compactionCount, 1, JSON.stringify(result));
    assert.equal(result.meta.contextManagement.lastTurnCompactions, 1);
    if (summaryExpected) assert.equal(scenario.summaries, 1);
    assert.ok(scenario.agentRequests >= 3, "The overflowed request must be retried after compaction.");
  }
  assert.equal(await fs.readFile(sentinelPath, "utf8"), sentinel);
  console.log(`PASS ${name}: ${scenario.agentRequests} agent requests, ${scenario.summaries} summaries`);
}

async function inspect(name, value, { active = true, hook = true, mode = "enabled" } = {}) {
  if (value === undefined) await fs.rm(configPath, { force: true });
  else await fs.writeFile(configPath, JSON.stringify(value));
  env.CLAWCTL_GATEWAY_ISOLATION = mode;
  const result = await run(["plugins", "inspect", "gateway-isolation", "--runtime", "--json"]);
  assert.equal(result.plugin.origin, "bundled", name);
  assert.equal(result.plugin.enabled, active, name);
  assert.equal(result.plugin.explicitlyEnabled, false, name);
  assert.equal(result.plugin.activated, active, name);
  assert.equal(result.plugin.imported, active, name);
  assert.equal(result.plugin.hookCount, hook ? 1 : 0, name);
  assert.deepEqual(result.typedHooks.map(entry => entry.name), hook ? ["before_prompt_build"] : [], name);
  assert.equal(result.httpRouteCount, active ? 1 : 0, name);
  if (value === undefined) {
    await assert.rejects(fs.stat(configPath), { code: "ENOENT" });
  } else {
    assert.equal(await fs.readFile(configPath, "utf8"), JSON.stringify(value), name);
  }
  console.log(`PASS ${name}`);
}

try {
  const configBytes = JSON.stringify(config);
  await fs.writeFile(configPath, configBytes);
  await agent("first turn");
  await agent("same-run compaction", { overflow: true });
  await agent("post-compaction turn");
  assert.equal(await fs.readFile(configPath, "utf8"), configBytes);
  config.agents.defaults.compaction.mode = "default";
  config.agents.defaults.compaction.recentTurnsPreserve = 0;
  const summaryConfigBytes = JSON.stringify(config);
  await fs.writeFile(configPath, summaryConfigBytes);
  await agent("same-run summary compaction", { overflow: true, summaryExpected: true });
  await agent("post-summary-compaction turn");
  await agent("new session", { sessionKey: "agent:main:another-fixture" });
  await agent("subagent session key", { sessionKey: "agent:main:subagent:fixture" });
  await agent("cron session key", { sessionKey: "agent:main:cron:fixture" });
  assert.equal(await fs.readFile(configPath, "utf8"), summaryConfigBytes);
  const denied = structuredClone(config);
  denied.plugins.entries = { "gateway-isolation": { hooks: { allowPromptInjection: false } } };
  await fs.writeFile(configPath, JSON.stringify(denied));
  await agent("prompt-injection opt-out", { guidance: false, sessionKey: "agent:main:opt-out-fixture" });
  await inspect("fresh profile", undefined);
  await inspect("existing undecided profile", {});
  await inspect("explicit disable", { plugins: { entries: { "gateway-isolation": { enabled: false } } } },
    { active: false, hook: false });
  await inspect("global disable", { plugins: { enabled: false } }, { active: false, hook: false });
  await inspect("denylist", { plugins: { deny: ["gateway-isolation"] } }, { active: false, hook: false });
  await inspect("restrictive allowlist", { plugins: { allow: ["openai"] } }, { active: false, hook: false });
  for (const flag of ["allowPromptInjection", "allowConversationAccess"]) {
    await inspect(flag, { plugins: { entries: { "gateway-isolation": { hooks: { [flag]: false } } } } },
      { hook: false });
  }
  for (const mode of ["", "disabled", "ENABLED", " enabled "]) {
    await inspect(`invalid report ${JSON.stringify(mode)}`, {}, { hook: false, mode });
  }
  for (const [name, text] of Object.entries(userFiles)) {
    assert.equal(await fs.readFile(path.join(workspace, name), "utf8"), text);
  }
  assert.equal(await fs.readFile(sentinelPath, "utf8"), sentinel);
  console.log("PASS user instructions and private original unchanged");
} finally {
  server.close();
  server.closeAllConnections();
  await fs.rm(root, { recursive: true, force: true });
}
