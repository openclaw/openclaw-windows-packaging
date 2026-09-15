import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import vm from "node:vm";
import {
  createGatewayIsolationPlugin,
  readGatewayIsolationMode,
  renderGatewayIsolationPage,
} from "./index.js";

test("ships disabled by default while retaining explicit startup activation", () => {
  const manifest = JSON.parse(
    fs.readFileSync(new URL("./openclaw.plugin.json", import.meta.url), "utf8"),
  );
  assert.equal(manifest.enabledByDefault, false);
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

test("accepts only the exact launcher isolation values", () => {
  assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: "enabled" }), "enabled");
  assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: "disabled" }), "disabled");
  for (const value of [undefined, "", "ENABLED", "unknown"]) {
    assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: value }), null);
  }
});

for (const expected of [
  {
    mode: "enabled",
    status: "Enabled",
    tone: "status--ok",
    command: "clawctl gateway-isolation disable",
  },
  {
    mode: "disabled",
    status: "Disabled",
    tone: "status--warn",
    command: "clawctl gateway-isolation enable",
  },
]) {
  test(`renders the exact ${expected.mode} read-only status`, () => {
    const html = renderGatewayIsolationPage(expected.mode);
    assert.match(html, /Windows Launcher/);
    assert.match(html, /Gateway Isolation/);
    assert.match(html, new RegExp(`>${expected.status}<`));
    assert.match(html, new RegExp(expected.tone));
    assert.match(html, /Change with CLI/);
    assert.match(
      html,
      /Command support is expected in a paired launcher update\./,
    );
    assert.match(
      html,
      /Run from the signed-in user session on the Gateway host after that support is installed\./,
    );
    assert.match(html, new RegExp(expected.command));
    assert.match(html, /aria-label="Copy command"/);
    assert.match(html, /Copy the selected command manually\./);
    assert.match(html, /copied = document\.execCommand\("copy"\)/);
    assert.match(html, /openclaw:widget-theme/);
    assert.doesNotMatch(html, /next manual Gateway restart/i);
  });
}

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
  assert.equal(bridge.properties.get("--button-bg"), "#303030");
  assert.equal(bridge.properties.get("--text"), "#fefefe");
  assert.equal(bridge.properties.get("--text-strong"), "#ffffff");
  assert.equal(bridge.properties.get("--focus"), "#55aaff");
  assert.equal(bridge.properties.get("--radius"), "14px");
  assert.equal(bridge.properties.get("--radius-full"), "9999px");
  assert.equal(bridge.properties.get("--font-body"), "Georgia, serif");
  assert.equal(bridge.properties.get("--font-mono"), "Consolas, monospace");
  assert.match(bridge.properties.get("--ok-bg"), /#44cc77 18%, #202020/);
  assert.match(bridge.properties.get("--warn-bg"), /#e0a020 18%, #202020/);
});

test("ignores theme messages from other frames and malformed host values", () => {
  const bridge = runThemeBridge(renderGatewayIsolationPage("disabled"));
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
  assert.equal(response.headers["Cache-Control"], "no-store");
  assert.match(response.headers["Content-Security-Policy"], /frame-ancestors 'self'/);
  assert.match(response.body, />Enabled</);
});

for (const initial of ["enabled", "disabled", undefined, "", "invalid", "ENABLED", " enabled "]) {
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
    assert.equal(first.statusCode, initial === "enabled" || initial === "disabled" ? 200 : 503);
    if (initial === "enabled") assert.match(first.body, />Enabled</);
    if (initial === "disabled") assert.match(first.body, />Disabled</);
    for (value of ["enabled", "disabled", "invalid", undefined]) {
      assert.deepEqual(invokeRoute(routes[0]), first);
    }
    assert.equal(reads, 1);
  });
}

for (const mode of [undefined, "", "invalid", "ENABLED", " enabled "]) {
  const label = JSON.stringify(mode) ?? "missing";
    test(`fails closed for ${label} with no status or mutation guidance`, () => {
    const { routes } = registerPlugin(mode);
    const response = invokeRoute(routes[0]);
    assert.equal(response.statusCode, 503);
    assert.match(response.body, /did not provide a valid Gateway isolation mode/);
    assert.doesNotMatch(
      response.body,
      /status--(?:ok|warn)|<button|Change with CLI|clawctl gateway-isolation/,
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
    assert.equal(response.headers["Cache-Control"], "no-store");
    assert.equal(response.headers["X-Content-Type-Options"], "nosniff");
    assert.equal(response.headers["Referrer-Policy"], "no-referrer");
  });
  test(`renderer rejects ${label}`, () => {
    assert.throws(() => renderGatewayIsolationPage(mode), TypeError);
  });
}
