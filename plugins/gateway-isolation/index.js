const ISOLATION_ENVIRONMENT_VARIABLE = "CLAWCTL_GATEWAY_ISOLATION";
const STATUS_PATH = "/plugins/gateway-isolation/status";
const THEME_MESSAGE_TYPE = "openclaw:widget-theme";
const THEME_BRIDGE_SCRIPT = `<script>
  const themeTokenProperties = {
    surface: "--bg",
    card: "--card",
    elevated: "--button-bg",
    text: "--text",
    "text-strong": "--text-strong",
    muted: "--muted",
    border: "--border",
    "border-strong": "--border-strong",
    accent: "--focus",
    ok: "--ok-text",
    warn: "--warn-text",
    radius: "--radius",
    "radius-full": "--radius-full",
    "font-body": "--font-body",
    "font-mono": "--font-mono",
  };
  window.addEventListener("message", (event) => {
    if (event.source !== window.parent) return;
    const message = event.data;
    if (
      !message ||
      message.type !== "${THEME_MESSAGE_TYPE}" ||
      (message.mode !== "light" && message.mode !== "dark") ||
      !message.tokens ||
      typeof message.tokens !== "object" ||
      Array.isArray(message.tokens)
    ) return;
    const root = document.documentElement;
    root.dataset.themeMode = message.mode;
    root.style.colorScheme = message.mode;
    for (const [token, property] of Object.entries(themeTokenProperties)) {
      const value = message.tokens[token];
      if (typeof value === "string" && value.trim() && value.length <= 256) {
        root.style.setProperty(property, value);
      }
    }
    const styles = getComputedStyle(root);
    const card = styles.getPropertyValue("--card").trim();
    const ok = styles.getPropertyValue("--ok-text").trim();
    const warn = styles.getPropertyValue("--warn-text").trim();
    if (card && ok) {
      root.style.setProperty("--ok-bg", "color-mix(in srgb, " + ok + " 18%, " + card + ")");
    }
    if (card && warn) {
      root.style.setProperty("--warn-bg", "color-mix(in srgb, " + warn + " 18%, " + card + ")");
    }
  });
</script>`;

export function readGatewayIsolationMode(env) {
  const value = env[ISOLATION_ENVIRONMENT_VARIABLE];
  return value === "enabled" || value === "disabled" ? value : null;
}

export function renderGatewayIsolationPage(mode) {
  if (mode !== "enabled" && mode !== "disabled") {
    throw new TypeError("Gateway isolation mode must be enabled or disabled.");
  }

  const enabled = mode === "enabled";
  const status = enabled ? "Enabled" : "Disabled";
  const command = `clawctl gateway-isolation ${enabled ? "disable" : "enable"}`;
  const tone = enabled ? "ok" : "warn";

  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Windows Launcher</title>
  <style>
    :root {
      color-scheme: light dark;
      --font-body: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      --font-mono: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
      --radius: 10px;
      --radius-full: 9999px;
      --bg: #ffffff;
      --card: #f6f8fa;
      --border: #d0d7de;
      --border-strong: #afb8c1;
      --text: #1f2328;
      --text-strong: #1f2328;
      --muted: #59636e;
      --ok-bg: #dafbe1;
      --ok-text: #116329;
      --warn-bg: #fff8c5;
      --warn-text: #7d4e00;
      --button-bg: #f6f8fa;
      --focus: #0969da;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --bg: #0d1117;
        --card: #161b22;
        --border: #30363d;
        --border-strong: #484f58;
        --text: #f0f6fc;
        --text-strong: #f0f6fc;
        --muted: #8b949e;
        --ok-bg: #12261e;
        --ok-text: #56d364;
        --warn-bg: #2e240d;
        --warn-text: #e3b341;
        --button-bg: #21262d;
        --focus: #58a6ff;
      }
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font-family: var(--font-body);
      font-size: 14px;
    }
    main {
      max-width: 880px;
      margin: 0 auto;
      padding: 24px;
    }
    h1 {
      margin: 0 0 8px;
      color: var(--text-strong);
      font-size: 20px;
      font-weight: 650;
    }
    .intro {
      margin: 0 0 20px;
      color: var(--muted);
      line-height: 1.5;
    }
    .settings-section {
      overflow: hidden;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--card);
    }
    .settings-row {
      display: grid;
      grid-template-columns: minmax(180px, 1fr) minmax(240px, 1.3fr);
      gap: 20px;
      align-items: center;
      padding: 18px;
    }
    .settings-row + .settings-row { border-top: 1px solid var(--border); }
    .settings-row--stacked { align-items: start; }
    .settings-row__title { color: var(--text-strong); font-weight: 600; }
    .settings-row__description {
      margin-top: 5px;
      color: var(--muted);
      line-height: 1.45;
    }
    .settings-row__control { justify-self: end; min-width: 0; }
    .status {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      border-radius: var(--radius-full);
      padding: 5px 10px;
      font-weight: 600;
    }
    .status::before {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      background: currentColor;
      content: "";
    }
    .status--ok { color: var(--ok-text); background: var(--ok-bg); }
    .status--warn { color: var(--warn-text); background: var(--warn-bg); }
    .command {
      display: flex;
      min-width: 0;
      align-items: center;
      gap: 8px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 8px 9px;
      background: var(--bg);
    }
    code {
      min-width: 0;
      overflow-wrap: anywhere;
      font-family: var(--font-mono);
      font-size: 13px;
    }
    button {
      flex: none;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 5px 9px;
      background: var(--button-bg);
      color: var(--text);
      cursor: pointer;
      font: inherit;
    }
    .copy-status {
      margin-top: 6px;
      color: var(--muted);
      font-size: 12px;
    }
    button:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    @media (max-width: 620px) {
      main { padding: 16px; }
      .settings-row { grid-template-columns: 1fr; gap: 12px; }
      .settings-row__control { justify-self: stretch; }
      .status { width: fit-content; }
    }
  </style>
</head>
<body>
  <main>
    <h1>Windows Launcher</h1>
    <p class="intro">Diagnostic launch mode reported by the Windows launcher.</p>
    <section class="settings-section" aria-label="Windows Launcher">
      <div class="settings-row">
        <div class="settings-row__title">Gateway Isolation</div>
        <div class="settings-row__control">
          <span class="status status--${tone}">${status}</span>
        </div>
      </div>
      <div class="settings-row settings-row--stacked">
        <div>
          <div class="settings-row__title">Change with CLI</div>
          <div class="settings-row__description">Command support is expected in a paired launcher update. Run from the signed-in user session on the Gateway host after that support is installed.</div>
        </div>
        <div class="settings-row__control command">
          <code id="isolation-command">${command}</code>
          <button id="copy-command" type="button" aria-label="Copy command">Copy</button>
        </div>
        <div id="copy-status" class="copy-status" role="status" aria-live="polite"></div>
      </div>
    </section>
  </main>
  ${THEME_BRIDGE_SCRIPT}
  <script>
    const button = document.getElementById("copy-command");
    const command = document.getElementById("isolation-command");
    const status = document.getElementById("copy-status");
    button.addEventListener("click", async () => {
      const value = command.textContent;
      let copied = false;
      try {
        await navigator.clipboard.writeText(value);
        copied = true;
      } catch {
        const selection = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(command);
        selection.removeAllRanges();
        selection.addRange(range);
        copied = document.execCommand("copy");
        if (copied) {
          selection.removeAllRanges();
        }
      }
      if (copied) {
        button.textContent = "Copied";
        status.textContent = "";
      } else {
        button.textContent = "Selected";
        status.textContent = "Copy the selected command manually.";
      }
    });
  </script>
</body>
</html>`;
}

function renderGatewayIsolationUnavailablePage() {
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Windows Launcher unavailable</title>
  <style>
    :root {
      color-scheme: light dark;
      --font-body: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      --font-mono: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
      --radius: 10px;
      --radius-full: 9999px;
      --bg: #ffffff;
      --text: #1f2328;
      --text-strong: #1f2328;
      --muted: #59636e;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --bg: #0d1117;
        --text: #f0f6fc;
        --text-strong: #f0f6fc;
        --muted: #8b949e;
      }
    }
    body {
      margin: 0;
      padding: 24px;
      background: var(--bg);
      color: var(--text);
      font-family: var(--font-body);
      font-size: 14px;
    }
    h1 { margin: 0 0 8px; color: var(--text-strong); font-size: 20px; }
    p { margin: 0; color: var(--muted); line-height: 1.5; }
  </style>
</head>
<body>
  <h1>Windows Launcher unavailable</h1>
  <p>The Windows launcher did not provide a valid Gateway isolation mode.</p>
  ${THEME_BRIDGE_SCRIPT}
</body>
</html>`;
}

function writeHtmlResponse(response, statusCode, html) {
  response.writeHead(statusCode, {
    "Cache-Control": "no-store",
    "Content-Security-Policy":
      "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'self'",
    "Content-Type": "text/html; charset=utf-8",
    "Referrer-Policy": "no-referrer",
    "X-Content-Type-Options": "nosniff",
  });
  response.end(html);
}

export function createGatewayIsolationPlugin(env = process.env) {
  const launchMode = readGatewayIsolationMode(env);

  return {
    id: "gateway-isolation",
    name: "Windows Launcher",
    description: "Reports the Windows launch mode selected for the running Gateway.",
    register(api) {
      api.session.controls.registerControlUiDescriptor({
        surface: "tab",
        id: "gateway-isolation",
        label: "Windows Launcher",
        description: "Read-only Windows Gateway isolation status.",
        icon: "shield-check",
        group: "control",
        order: 20,
        path: STATUS_PATH,
        requiredScopes: ["operator.read"],
      });
      api.registerHttpRoute({
        path: STATUS_PATH,
        auth: "gateway",
        match: "exact",
        handler(_request, response) {
          if (!launchMode) {
            writeHtmlResponse(
              response,
              503,
              renderGatewayIsolationUnavailablePage(),
            );
            return true;
          }
          writeHtmlResponse(response, 200, renderGatewayIsolationPage(launchMode));
          return true;
        },
      });
    },
  };
}

export default createGatewayIsolationPlugin();
