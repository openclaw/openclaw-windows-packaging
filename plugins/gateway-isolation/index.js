const ISOLATION_ENVIRONMENT_VARIABLE = "CLAWCTL_GATEWAY_ISOLATION";
const STATUS_PATH = "/plugins/gateway-isolation/status";
const THEME_MESSAGE_TYPE = "openclaw:widget-theme";
const ISOLATION_AGENT_CONTEXT = [
  "## Windows agent session",
  "This Gateway and tools executing locally here run in a separate Windows agent session, not the user's interactive desktop. A tool target called host or gateway does not by itself mean the user's session.",
  "Dialogs, browser windows, and other GUI started here are not visible or interactive for the user. Authorized agent-only GUI work is allowed when it requires no human viewing or input; do not assume GUI automation capabilities are available. Do not launch or offer to launch local GUI for user participation, even when the user asks you to open a window or sign-in dialog. Instead, give steps for the user to act on their own desktop, or use their connected chat, web, TUI, or other supported client. When participation is needed, prefer a supported CLI, headless, device-code, or text workflow; relay safe steps or verification URLs and wait for the required response. If the supported workflow is unknown, ask for its documentation instead of promising a local dialog. Do not invent authentication flows, collect or expose credentials/tokens, bypass MFA/consent, or disable isolation. Send sensitive verification material only through a supported private route to the requesting user.",
  "Keep scratch files, dependencies, repositories, and working trees private when appropriate. A path existing or being readable here does not prove user access. Hand off only intended nonsensitive deliverables through a supported client attachment/export or a narrow filesystem copy. For requested in-chat delivery, verify the exact current file and use a supported attachment, or explain why delivery is unavailable; a saved copy, path, or earlier attachment is not delivery. Preserve private originals; do not export secrets, caches, or whole workspaces by default. Verify recipient-side access or delivery where feasible, distinguish filesystem access from client delivery, and state what remains unverified.",
  "For filesystem handoff, prefer the host-reported or user-selected destination. If neither is provided, use the isolated agent account's existing Shared folder: use a local tool inside this Windows agent session to resolve it, for example (Resolve-Path -LiteralPath (Join-Path $env:USERPROFILE 'Shared') -ErrorAction Stop).Path in PowerShell. Verify that the resolved path is an existing directory. Do not expand the human user's USERPROFILE, substitute the OpenClaw workspace or state directory, or hard-code an account path. Before copying there, show the complete resolved destination in a fenced text code block, not an inline path or file link; say the user can choose another destination. Do not copy to a destination the user declines. Copy rather than move the intended deliverables; do not overwrite unrelated files. A successful copy is not verified recipient access. State what access remains unverified. If profile or Shared-folder resolution fails, the directory is missing, or access or copying fails, report the failure and ask for a supported destination. Do not fall back to Public Documents or PUBLIC/TEMP, create a replacement shared root, use administrator Explorer, broad ACL changes, or weaken isolation.",
  "Remote and user-session nodes have their own filesystem and UI context. Use a UI route only when its execution location, capability, and authorization are established; a connected client or node alone proves none of these. Explain these limits when they affect the user's next action, not on every turn.",
].join("\n\n");
const COMMAND_GROUPS = [
  {
    id: "clawctl",
    title: "ClawCtl",
    commands: [
      {
        id: "gateway-status",
        title: "Gateway status",
        description: "Show whether the gateway is running.",
        command: "clawctl gateway-service status",
      },
      {
        id: "gateway-restart",
        title: "Restart Gateway",
        description: "Restart the gateway, keeping the session and its data. Starts it if no gateway is running.",
        command: "clawctl gateway-service restart",
      },
      {
        id: "dashboard",
        title: "Open dashboard",
        description: "Open the dashboard in your default browser. Requires completed setup and a running gateway. Does not print authenticated URLs or tokens.",
        command: "clawctl open",
      },
      {
        id: "agent-shell",
        title: "Agent session PowerShell",
        description: "Open PowerShell inside the isolated agent. openclaw and node are available there; clawctl manages the session from outside it.",
        command: "clawctl pwsh",
      },
      {
        id: "launcher-help",
        title: "Launcher help",
        description: "List launcher commands.",
        command: "clawctl --help",
      },
    ],
  },
  {
    id: "openclaw",
    title: "OpenClaw",
    description: "The packaged openclaw command forwards to your agent session. Inside clawctl pwsh, it runs directly.",
    commands: [
      {
        id: "terminal",
        title: "Gateway chat TUI",
        description: "Chat in the terminal.",
        command: "openclaw tui",
      },
      {
        id: "openclaw-help",
        title: "OpenClaw help",
        description: "List OpenClaw commands.",
        command: "openclaw --help",
      },
    ],
  },
];
const THEME_BRIDGE_SCRIPT = `<script>
  const themeTokenProperties = {
    surface: "--bg",
    card: "--card",
    elevated: "--button-bg",
    text: "--text",
    "text-strong": "--text-strong",
    muted: "--muted",
    border: "--border",
    accent: "--focus",
    ok: "--ok-text",
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
    if (card && ok) {
      root.style.setProperty("--ok-bg", "color-mix(in srgb, " + ok + " 18%, " + card + ")");
    }
  });
</script>`;
const COPY_COMMAND_SCRIPT = `<script>
  let copyRequest = 0;
  const feedback = document.getElementById("copy-status");
  for (const button of document.querySelectorAll("[data-copy-command]")) {
    button.addEventListener("click", async () => {
      const request = ++copyRequest;
      const command = document.getElementById(button.dataset.copyCommand);
      const title = button.dataset.commandTitle;
      let copied = false;
      let selected = false;
      let selection;
      const previousRanges = [];
      feedback.textContent = "";
      // Copy synchronously while the click has user activation. The opaque
      // sandbox cannot rely on permission to use the async Clipboard API.
      try {
        selection = window.getSelection();
        for (let i = 0; i < selection.rangeCount; i++) {
          previousRanges.push(selection.getRangeAt(i).cloneRange());
        }
        const range = document.createRange();
        range.selectNodeContents(command);
        selection.removeAllRanges();
        selection.addRange(range);
        selected = true;
        copied = document.execCommand("copy");
      } catch {
        copied = false;
      }
      if (!copied) {
        try {
          await navigator.clipboard.writeText(command.textContent);
          copied = true;
        } catch {
          copied = false;
        }
      }
      if (request !== copyRequest) return;
      if (copied) {
        if (selected) {
          selection.removeAllRanges();
          for (const range of previousRanges) selection.addRange(range);
        }
        feedback.textContent = "Copied " + title + " command.";
      } else {
        feedback.textContent = selected
          ? "Copy unavailable. " + title + " command selected; press Ctrl+C to copy."
          : "Could not copy " + title + " command. Select the command and copy it manually.";
      }
    });
  }
</script>`;

function renderCommandReferences() {
  const groups = COMMAND_GROUPS.map((group) => {
    const rows = group.commands.map(({ id, title, description, command }) => `
      <div class="command-row">
        <div>
          <h4>${title}</h4>
          <p class="command-description">${description}</p>
        </div>
        <div class="command">
          <code id="command-${id}">${command.replaceAll("&", "&amp;")}</code>
          <button type="button" data-copy-command="command-${id}" data-command-title="${title}" aria-label="Copy ${title} command">Copy</button>
        </div>
      </div>`).join("");
    return `
    <section aria-labelledby="${group.id}-title">
      <h3 id="${group.id}-title" class="command-group-title">${group.title}</h3>
      ${group.description ? `<p class="intro">${group.description}</p>` : ""}
      <div class="status-section">${rows}
      </div>
    </section>`;
  }).join("");
  return `<h2 class="command-reference-title">Command reference</h2>
    <p class="intro">Run these commands in your normal Windows terminal (user session).</p>
    ${groups}
    <p id="copy-status" class="copy-status" role="status" aria-live="polite" aria-atomic="true"></p>`;
}

export function readGatewayIsolationMode(env) {
  const value = env[ISOLATION_ENVIRONMENT_VARIABLE];
  return value === "enabled" ? value : null;
}

export function renderGatewayIsolationPage(mode) {
  if (mode !== "enabled") {
    throw new TypeError("Gateway isolation mode must be enabled.");
  }

  return renderStatusPage(true);
}

function renderStatusPage(enabled) {
  const description = enabled
    ? "This Gateway is running in Windows isolation."
    : "Isolation status is unavailable. The Windows launcher did not provide a valid isolation report.";
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
      --font-mono: ui-monospace, SFMono-Regular, "Cascadia Code", "Liberation Mono", monospace;
      --radius: 10px;
      --radius-full: 9999px;
      --bg: #ffffff;
      --card: #f6f8fa;
      --border: #d0d7de;
      --text: #1f2328;
      --text-strong: #1f2328;
      --muted: #59636e;
      --ok-bg: #dafbe1;
      --ok-text: #116329;
      --button-bg: #f6f8fa;
      --focus: #0969da;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --bg: #0d1117;
        --card: #161b22;
        --border: #30363d;
        --text: #f0f6fc;
        --text-strong: #f0f6fc;
        --muted: #8b949e;
        --ok-bg: #12261e;
        --ok-text: #56d364;
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
    .status-section {
      overflow: hidden;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--card);
    }
    .status-row {
      display: grid;
      grid-template-columns: minmax(180px, 1fr) minmax(240px, 1.3fr);
      gap: 20px;
      align-items: center;
      padding: 18px;
    }
    dt { color: var(--text-strong); font-weight: 600; }
    dd { margin: 0; justify-self: end; min-width: 0; }
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
    .status--neutral { color: var(--muted); }
    h2 { margin: 28px 0 8px; color: var(--text-strong); font-size: 17px; }
    .command-reference-title { font-size: 20px; }
    .command-group-title { margin: 20px 0 8px; color: var(--text-strong); font-size: 17px; font-weight: 600; }
    .command-row h4 { margin: 0; color: var(--text-strong); font-size: 14px; font-weight: 600; }
    .command-row {
      display: grid;
      grid-template-columns: minmax(180px, 1fr) minmax(240px, 1.4fr);
      align-items: center;
      gap: 20px;
      padding: 12px 18px;
    }
    .command-row + .command-row { border-top: 1px solid var(--border); }
    .command-description { margin: 5px 0 0; color: var(--muted); font-size: 13px; line-height: 1.45; }
    .command {
      display: flex;
      align-items: center;
      gap: 10px;
      min-width: 0;
      padding: 8px 10px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--bg);
    }
    code { flex: 1; min-width: 0; overflow-wrap: anywhere; font: 13px/1.5 var(--font-mono); }
    button {
      flex: none;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 7px 10px;
      background: var(--button-bg);
      color: var(--text);
      cursor: pointer;
      font: inherit;
    }
    button:hover { border-color: var(--text); }
    button:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    .copy-status { min-height: 1.5em; margin: 8px 0 0; color: var(--muted); font-size: 13px; line-height: 1.5; }
    @media (max-width: 620px) {
      main { padding: 16px; }
      .status-row { grid-template-columns: 1fr; gap: 12px; }
      dd { justify-self: start; }
      .status { width: fit-content; }
      .command-row { grid-template-columns: 1fr; gap: 12px; }
    }
  </style>
</head>
<body>
  <main>
    <h1>Windows Launcher</h1>
    <p class="intro">${description}</p>
    <dl class="status-section" aria-label="Windows Launcher status">
      <div class="status-row">
        <dt>Gateway Isolation</dt>
        <dd><span class="status status--${enabled ? "ok" : "neutral"}">${enabled ? "Active" : "Invalid"}</span></dd>
      </div>
    </dl>
    ${enabled ? renderCommandReferences() : ""}
  </main>
  ${THEME_BRIDGE_SCRIPT}
  ${enabled ? COPY_COMMAND_SCRIPT : ""}
</body>
</html>`;
}

function renderGatewayIsolationUnavailablePage() {
  return renderStatusPage(false);
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

export function createGatewayIsolationPlugin(env = process.env, platform = process.platform) {
  const launchMode = readGatewayIsolationMode(env);

  return {
    id: "gateway-isolation",
    name: "Windows Launcher",
    description: "Reports Windows isolation status and supplies local-session agent guidance.",
    register(api) {
      if (platform === "win32" && launchMode === "enabled") {
        // Keep the user-context fallback: v2026.9.4 replaces system additions on runtime-only turns.
        api.on("before_prompt_build", () => ({
          prependContext: ISOLATION_AGENT_CONTEXT,
          appendSystemContext: ISOLATION_AGENT_CONTEXT,
        }));
      }
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
