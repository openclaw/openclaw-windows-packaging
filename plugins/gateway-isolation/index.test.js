import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import vm from "node:vm";
import {
  createGatewayIsolationPlugin,
  readGatewayIsolationMode,
  renderGatewayIsolationPage,
} from "./index.js";

test("ships enabled by default with startup activation", () => {
  const manifest = JSON.parse(
    fs.readFileSync(new URL("./openclaw.plugin.json", import.meta.url), "utf8"),
  );
  assert.equal(manifest.enabledByDefault, true);
  assert.equal(manifest.enabledByDefaultOnPlatforms, undefined);
  assert.equal(manifest.activation.onStartup, true);
});

function registerPlugin(mode) {
  const descriptors = [];
  const routes = [];
  const plugin = createGatewayIsolationPlugin({
    CLAWCTL_GATEWAY_ISOLATION: mode,
  });
  plugin.register({
    session: {
      controls: {
        registerControlUiDescriptor(descriptor) {
          descriptors.push(descriptor);
        },
      },
    },
    registerHttpRoute(route) {
      routes.push(route);
    },
  });
  assert.equal(descriptors.length, 1);
  assert.equal(routes.length, 1);
  return { descriptors, routes };
}

function invokeRoute(route) {
  const result = {
    body: "",
    headers: {},
    statusCode: 0,
  };
  const handled = route.handler(
    {},
    {
      writeHead(statusCode, headers) {
        result.statusCode = statusCode;
        result.headers = headers;
      },
      end(body) {
        result.body = body;
      },
    },
  );
  assert.equal(handled, true);
  return result;
}

function runThemeBridge(html) {
  const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)];
  assert.ok(scripts.length >= 1);
  const properties = new Map();
  const root = {
    dataset: {},
    style: {
      colorScheme: "",
      setProperty(name, value) {
        properties.set(name, value);
      },
    },
  };
  const parent = {};
  let listener;
  const context = {
    document: { documentElement: root },
    getComputedStyle() {
      return {
        getPropertyValue(name) {
          return properties.get(name) ?? "";
        },
      };
    },
    window: {
      parent,
      addEventListener(type, callback) {
        if (type === "message") listener = callback;
      },
    },
  };
  vm.runInNewContext(scripts[0][1], context);
  assert.equal(typeof listener, "function");
  return { listener, parent, properties, root };
}

const invalidModes = [
  undefined, null, "", "disabled", "invalid", "ENABLED", " enabled ",
  "1", true, 1, "<script>alert(1)</script>",
];

function assertInformationalPage(html) {
  assert.match(html, /<title>Windows Launcher<\/title>/);
  assert.match(html, /<h1>Windows Launcher<\/h1>/);
  assert.match(html, /<dl[^>]+aria-label="Windows Launcher status"/);
  assert.equal([...html.matchAll(/<dt>/g)].length, 1);
  assert.match(html, /<dt>Gateway Isolation<\/dt>/);
  assert.doesNotMatch(html, />Running<|<dt>Gateway<\/dt>|<dt>Isolation<\/dt>/);
  assert.doesNotMatch(
    html,
    /<input|<select|<form|Change with CLI|--no-isolation|Disabled|Not running|clawctl gateway isolation|fetch\(|XMLHttpRequest|WebSocket/i,
  );
}

function assertHeaders(response) {
  assert.deepEqual(response.headers, {
    "Cache-Control": "no-store",
    "Content-Security-Policy":
      "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'self'",
    "Content-Type": "text/html; charset=utf-8",
    "Referrer-Policy": "no-referrer",
    "X-Content-Type-Options": "nosniff",
  });
}

test("accepts only the exact enabled launcher report", () => {
  assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: "enabled" }), "enabled");
  for (const value of invalidModes) {
    assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: value }), null);
  }
});

test("does not use session-routing preference as isolation evidence", () => {
  for (const mode of invalidModes) {
    assert.equal(readGatewayIsolationMode({
      CLAWCTL_GATEWAY_ISOLATION: mode,
      OPENCLAW_SESSION: "1",
    }), null);
  }
  assert.equal(readGatewayIsolationMode({
    CLAWCTL_GATEWAY_ISOLATION: "enabled",
    get OPENCLAW_SESSION() {
      throw new Error("Routing preference must not be read.");
    },
  }), "enabled");
});

test("renders one informational Gateway Isolation row with a single active badge", () => {
  const html = renderGatewayIsolationPage("enabled");
  assertInformationalPage(html);
  assert.match(html, /<dt>Gateway Isolation<\/dt>\s*<dd><span class="status status--ok">Active<\/span><\/dd>/);
  assert.equal([...html.matchAll(/class="status status--ok"/g)].length, 1);
  assert.match(html, /This Gateway is running in Windows isolation\./);
  assert.doesNotMatch(html, />Invalid<|Isolation status is unavailable/);
});

const expectedCommands = [
  ["Gateway status", "clawctl gateway-service status"],
  ["Restart Gateway", "clawctl gateway-service restart"],
  ["Open dashboard", "clawctl open"],
  ["Agent session PowerShell", "clawctl pwsh"],
  ["Launcher help", "clawctl --help"],
  ["Gateway chat TUI", "openclaw tui"],
  ["OpenClaw help", "openclaw --help"],
];

test("groups the brief cheat sheet with ClawCtl first and packaged OpenClaw second", () => {
  const html = renderGatewayIsolationPage("enabled");
  assert.deepEqual(
    [...html.matchAll(/<h([1-6])(?: [^>]*)?>([^<]+)<\/h\1>/g)].map(match => [Number(match[1]), match[2]]),
    [
      [1, "Windows Launcher"],
      [2, "Command reference"],
      [3, "ClawCtl"],
      ...expectedCommands.slice(0, 5).map(([title]) => [4, title]),
      [3, "OpenClaw"],
      ...expectedCommands.slice(5).map(([title]) => [4, title]),
    ],
  );
  const groups = [...html.matchAll(/<section aria-labelledby="([^"]+)">([\s\S]*?)<\/section>/g)];
  assert.deepEqual(groups.map(match => match[1]), ["clawctl-title", "openclaw-title"]);
  assert.match(groups[0][2], /<h3 id="clawctl-title" class="command-group-title">ClawCtl<\/h3>/);
  assert.match(html, /<h2 class="command-reference-title">Command reference<\/h2>\s*<p class="intro">Run these commands in your normal Windows terminal \(user session\)\.<\/p>\s*<section aria-labelledby="clawctl-title">/);
  assert.match(groups[1][2], /<h3 id="openclaw-title" class="command-group-title">OpenClaw<\/h3>\s*<p class="intro">The packaged openclaw command forwards to your agent session\. Inside clawctl pwsh, it runs directly\.<\/p>/);
  for (const [index, expected] of [expectedCommands.slice(0, 5), expectedCommands.slice(5)].entries()) {
    assert.deepEqual(
      [...groups[index][2].matchAll(/<h4>([^<]+)<\/h4>/g)].map(match => match[1]),
      expected.map(([title]) => title),
    );
  }
  assert.deepEqual(
    [...html.matchAll(/<p class="command-description">([^<]+)<\/p>/g)].map(match => match[1]),
    [
      "Show whether the gateway is running.",
      "Restart the gateway, keeping the session and its data. Starts it if no gateway is running.",
      "Open the dashboard in your default browser. Requires completed setup and a running gateway. Does not print authenticated URLs or tokens.",
      "Open PowerShell inside the isolated agent. openclaw and node are available there; clawctl manages the session from outside it.",
      "List launcher commands.",
      "Chat in the terminal.",
      "List OpenClaw commands.",
    ],
  );
});

test("offers only supported general commands with individually named copy controls", () => {
  const html = renderGatewayIsolationPage("enabled");
  const commands = [...html.matchAll(/<code id="([^"]+)">([^<]+)<\/code>/g)];
  assert.deepEqual(commands.map(match => match[2].replaceAll("&amp;", "&")), expectedCommands.map(([, command]) => command));
  assert.equal([...html.matchAll(/<button /g)].length, expectedCommands.length);
  for (const [index, [title]] of expectedCommands.entries()) {
    assert.ok(html.includes(`data-copy-command="${commands[index][1]}" data-command-title="${title}" aria-label="Copy ${title} command"`));
  }
  assert.match(html, /role="status" aria-live="polite" aria-atomic="true"/);
  assert.equal([...html.matchAll(/id="copy-status"/g)].length, 1);
  assert.match(html, /button:focus-visible/);
  assert.match(html, /@media \(max-width: 620px\)/);
  assert.doesNotMatch(html, /clawctl tui|clawctl tty|clawctl dashboard|gateway-service stop|openclaw dashboard|--no-open|--no-browser|help --all|--help --all/);
});

function runCopyScript({ legacy = true, selectionAvailable = true, modern = "missing" } = {}) {
  const html = renderGatewayIsolationPage("enabled");
  const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)];
  assert.equal(scripts.length, 2);
  const feedback = { textContent: "" };
  const commands = [...html.matchAll(/<code id="([^"]+)">([^<]+)<\/code>/g)].map(match => ({
    id: match[1], textContent: match[2].replaceAll("&amp;", "&"),
  }));
  const buttons = commands.map((command, index) => ({
    dataset: { copyCommand: command.id, commandTitle: expectedCommands[index][0] },
    addEventListener(event, handler) {
      assert.equal(event, "click");
      this.click = handler;
    },
  }));
  let selected;
  const calls = [];
  const selection = {
    rangeCount: 0,
    removeAllRanges() { selected = undefined; },
    addRange(range) { selected = range.command; },
  };
  vm.runInNewContext(scripts[1][1], {
    document: {
      getElementById(id) { return id === "copy-status" ? feedback : commands.find(command => command.id === id); },
      querySelectorAll(selector) {
        assert.equal(selector, "[data-copy-command]");
        return buttons;
      },
      createRange() { return { selectNodeContents(command) { this.command = command; } }; },
      execCommand(command) {
        assert.equal(command, "copy");
        calls.push(["legacy", selected.textContent]);
        if (legacy === "throw") throw new Error("Copy denied");
        return legacy;
      },
    },
    window: { getSelection() { return selectionAvailable ? selection : null; } },
    navigator: modern === "missing" ? {} : {
      clipboard: {
        async writeText(text) {
          calls.push(["modern", text]);
          if (modern === "reject") throw new Error("Permission denied");
        },
      },
    },
  });
  return { buttons, feedback, calls, get selected() { return selected; } };
}

test("copies every exact command synchronously and announces only successful copies", async () => {
  const copy = runCopyScript();
  for (const [index, [title, command]] of expectedCommands.entries()) {
    await copy.buttons[index].click();
    assert.equal(copy.feedback.textContent, `Copied ${title} command.`);
    assert.deepEqual(copy.calls.at(-1), ["legacy", command]);
    assert.equal(copy.selected, undefined);
  }
});

for (const legacy of [false, "throw"]) {
  test(`uses Clipboard API when legacy copy returns ${legacy}`, async () => {
    const copy = runCopyScript({ legacy, modern: "success" });
    for (const title of ["Restart Gateway", "Open dashboard"]) {
      const index = expectedCommands.findIndex(([commandTitle]) => commandTitle === title);
      await copy.buttons[index].click();
      assert.equal(copy.feedback.textContent, `Copied ${title} command.`);
      assert.deepEqual(copy.calls.at(-1), ["modern", expectedCommands[index][1]]);
    }
  });
  for (const modern of ["missing", "reject"]) {
    test(`offers selected manual copy when legacy=${legacy} and modern=${modern}`, async () => {
      const copy = runCopyScript({ legacy, modern });
      for (const index of [3, 1, 2, 3]) {
        await copy.buttons[index].click();
        assert.equal(copy.feedback.textContent, `Copy unavailable. ${expectedCommands[index][0]} command selected; press Ctrl+C to copy.`);
        assert.equal(copy.selected.textContent, expectedCommands[index][1]);
        assert.doesNotMatch(copy.feedback.textContent, /Copied/);
      }
    });
  }
}

test("reports inability to select or copy without claiming manual selection succeeded", async () => {
  const copy = runCopyScript({ selectionAvailable: false });
  await copy.buttons[3].click();
  assert.equal(copy.feedback.textContent, "Could not copy Agent session PowerShell command. Select the command and copy it manually.");
  assert.equal(copy.selected, undefined);
});

test("can use Clipboard API without DOM selection support", async () => {
  const copy = runCopyScript({ selectionAvailable: false, modern: "success" });
  await copy.buttons[3].click();
  assert.equal(copy.feedback.textContent, "Copied Agent session PowerShell command.");
  assert.deepEqual(copy.calls, [["modern", "clawctl pwsh"]]);
});

test("applies recognized host theme tokens from the parent frame", () => {
  const bridge = runThemeBridge(renderGatewayIsolationPage("enabled"));
  bridge.listener({
    source: bridge.parent,
    data: {
      type: "openclaw:widget-theme",
      mode: "dark",
      tokens: {
        surface: "#101010",
        card: "#202020",
        elevated: "#303030",
        text: "#fefefe",
        muted: "#aaaaaa",
        border: "#404040",
        "border-strong": "#505050",
        accent: "#55aaff",
        ok: "#44cc77",
        warn: "#e0a020",
        radius: "14px",
        "radius-full": "9999px",
        "font-body": "Georgia, serif",
        "font-mono": "Consolas, monospace",
        "text-strong": "#ffffff",
      },
    },
  });

  assert.equal(bridge.root.dataset.themeMode, "dark");
  assert.equal(bridge.root.style.colorScheme, "dark");
  assert.equal(bridge.properties.get("--bg"), "#101010");
  assert.equal(bridge.properties.get("--card"), "#202020");
  assert.equal(bridge.properties.get("--text"), "#fefefe");
  assert.equal(bridge.properties.get("--text-strong"), "#ffffff");
  assert.equal(bridge.properties.get("--radius"), "14px");
  assert.equal(bridge.properties.get("--radius-full"), "9999px");
  assert.equal(bridge.properties.get("--font-body"), "Georgia, serif");
  assert.match(bridge.properties.get("--ok-bg"), /#44cc77 18%, #202020/);
  assert.equal(bridge.properties.get("--button-bg"), "#303030");
  assert.equal(bridge.properties.get("--focus"), "#55aaff");
  assert.equal(bridge.properties.get("--font-mono"), "Consolas, monospace");
  for (const unused of ["--warn-text", "--warn-bg"]) {
    assert.equal(bridge.properties.has(unused), false);
  }

  bridge.listener({
    source: bridge.parent,
    data: {
      type: "openclaw:widget-theme",
      mode: "light",
      tokens: { surface: "#fafafa", card: "#ffffff", ok: "#116329" },
    },
  });
  assert.equal(bridge.root.dataset.themeMode, "light");
  assert.equal(bridge.root.style.colorScheme, "light");
  assert.equal(bridge.properties.get("--bg"), "#fafafa");
  assert.match(bridge.properties.get("--ok-bg"), /#116329 18%, #ffffff/);
});

test("ignores theme messages from other frames and malformed host values", () => {
  const bridge = runThemeBridge(renderGatewayIsolationPage("enabled"));
  for (const event of [
    {
      source: {},
      data: { type: "openclaw:widget-theme", mode: "light", tokens: { surface: "#fff" } },
    },
    {
      source: bridge.parent,
      data: { type: "other", mode: "light", tokens: { surface: "#fff" } },
    },
    {
      source: bridge.parent,
      data: { type: "openclaw:widget-theme", mode: "sepia", tokens: { surface: "#fff" } },
    },
    {
      source: bridge.parent,
      data: { type: "openclaw:widget-theme", mode: "light", tokens: null },
    },
    {
      source: bridge.parent,
      data: { type: "openclaw:widget-theme", mode: "light", tokens: [] },
    },
  ]) {
    bridge.listener(event);
  }
  assert.equal(bridge.root.style.colorScheme, "");
  assert.deepEqual([...bridge.properties], []);
  bridge.listener({
    source: bridge.parent,
    data: {
      type: "openclaw:widget-theme",
      mode: "light",
      tokens: {
        surface: "",
        card: " ",
        text: 123,
        muted: "x".repeat(257),
        radius: null,
        unknown: "red",
      },
    },
  });
  assert.deepEqual([...bridge.properties], []);
});

test("registers one read-only Control tab and one authenticated sandbox route", () => {
  const { descriptors, routes } = registerPlugin("enabled");
  assert.deepEqual(descriptors[0], {
    surface: "tab",
    id: "gateway-isolation",
    label: "Windows Launcher",
    description: "Read-only Windows Gateway isolation status.",
    icon: "shield-check",
    group: "control",
    order: 20,
    path: "/plugins/gateway-isolation/status",
    requiredScopes: ["operator.read"],
  });
  assert.equal(routes[0].path, descriptors[0].path);
  assert.equal(routes[0].auth, "gateway");
  assert.equal(routes[0].match, "exact");

  const response = invokeRoute(routes[0]);
  assert.equal(response.statusCode, 200);
  assertHeaders(response);
  assertInformationalPage(response.body);
  assert.match(response.body, />Active</);
});

for (const initial of ["enabled", ...invalidModes]) {
  test(`reads ${JSON.stringify(initial) ?? "missing"} exactly once and keeps every response stable`, () => {
    let reads = 0;
    let value = initial;
    const plugin = createGatewayIsolationPlugin({
      get CLAWCTL_GATEWAY_ISOLATION() {
        reads++;
        return value;
      },
    });
    assert.equal(reads, 1);
    value = initial === "enabled" ? "disabled" : "enabled";
    const routes = [];
    plugin.register({
      session: { controls: { registerControlUiDescriptor() {} } },
      registerHttpRoute(route) {
        routes.push(route);
      },
    });

    const first = invokeRoute(routes[0]);
    assert.equal(first.statusCode, initial === "enabled" ? 200 : 503);
    assert.match(first.body, initial === "enabled" ? />Active</ : />Invalid</);
    assertInformationalPage(first.body);
    assertHeaders(first);
    for (value of ["enabled", "disabled", "invalid", undefined]) {
      assert.deepEqual(invokeRoute(routes[0]), first);
    }
    assert.equal(reads, 1);
  });
}

for (const mode of invalidModes) {
  const label = JSON.stringify(mode) ?? "missing";
  test(`fails closed for ${label} with neutral invalid status and no mutation guidance`, () => {
    const { routes } = registerPlugin(mode);
    const response = invokeRoute(routes[0]);
    assert.equal(response.statusCode, 503);
    assertInformationalPage(response.body);
    assert.match(response.body, /Isolation status is unavailable\./);
    assert.match(response.body, /did not provide a valid isolation report/);
    assert.match(response.body, /<dt>Gateway Isolation<\/dt>\s*<dd><span class="status status--neutral">Invalid<\/span><\/dd>/);
    assert.doesNotMatch(
      response.body,
      />Active<|class="status status--ok"|status--warn|<script>alert|<button|<code|clipboard|execCommand|getSelection|createRange|aria-live|id="copy-status"|clawctl|Command reference|<section|<h2/i,
    );
    const bridge = runThemeBridge(response.body);
    bridge.listener({
      source: bridge.parent,
      data: {
        type: "openclaw:widget-theme",
        mode: "dark",
        tokens: { surface: "#101010", text: "#fefefe", muted: "#aaaaaa" },
      },
    });
    assert.equal(bridge.root.style.colorScheme, "dark");
    assert.equal(bridge.properties.get("--bg"), "#101010");
    assert.equal(bridge.properties.get("--text"), "#fefefe");
    assert.equal(bridge.properties.get("--muted"), "#aaaaaa");
    assertHeaders(response);
  });
  test(`renderer rejects ${label}`, () => {
    assert.throws(() => renderGatewayIsolationPage(mode), TypeError);
  });
}
