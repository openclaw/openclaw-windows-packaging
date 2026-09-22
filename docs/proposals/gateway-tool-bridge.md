# Gateway tool bridge design

## Status

Draft implementation design. This proposal is intended to be implemented with the companion Windows Hub design in [openclaw/openclaw-windows-node](https://github.com/openclaw/openclaw-windows-node).

## Problem

The packaged Gateway runs in an MXC-isolated agent session. A command available in the interactive Windows user's PATH, profile, or Credential Manager is therefore not automatically available to the Gateway. Users need to be able to install a tool using its official distribution, then deliberately make that existing tool available to the isolated Gateway without importing their desktop environment.

## Scope

This package owns the authoritative tool bridge. It will expose a versioned local broker for the interactive Hub and will:

- register a user-approved executable under a safe command alias;
- support a desktop-installed executable and an executable placed in a stable package-managed **Gateway Tools** location;
- validate the canonical executable path and command alias, prevent alias collisions, and record the approved registration;
- create Gateway-only command shims and prepend only their directory to Gateway child-process `PATH`;
- verify every registration from the actual isolated Gateway session with a bounded, non-mutating command;
- create a per-registration Gateway runtime profile for tools that support configurable config, state, or credential storage;
- start a desktop-interactive setup session with that runtime profile, so browser and loopback OAuth can complete in the human session while the resulting supported state is readable by Gateway;
- return bounded, redacted status to the Hub and restart or refresh Gateway only when a registration change requires it.

The first expected consumer is `gog`, but this protocol and storage model are provider-neutral.

## Installation sources

The registration record distinguishes executable source from runtime profile ownership:

| Source | User action | Gateway behavior |
| --- | --- | --- |
| Desktop or machine installation | Select an existing executable in Hub | Broker verifies that exact path in the isolated session and creates a shim only if it works. |
| Gateway Tools | Extract an official provider ZIP into the stable package-managed folder, then scan/register it in Hub | Broker scans and verifies it from the isolated session before creating a shim. |

The product must not expose the MXC account name or ask users to install into an unstable `C:\Users\<agent>` path. The package owns the physical Gateway Tools location, its lifecycle, and its ACLs.

## Broker contract

The launcher and session protocol will define a narrow versioned local contract. The Hub is an authenticated local client, not the policy owner.

```text
listTools
registerTool
scanGatewayTools
verifyTool
setToolEnabled
unregisterTool
createRuntimeProfile
startInteractiveSetup
```

A registration contains only non-secret, bounded state:

```json
{
  "registrationId": "toolreg_opaque",
  "command": "gog",
  "displayName": "Google Workspace CLI",
  "executableSource": "desktop",
  "enabled": true,
  "runtimeProfileId": "toolprofile_opaque",
  "gatewayVerification": "ready"
}
```

Raw executable paths remain local broker state and are never sent to agent chat/model context. Verification output is bounded and redacted.

## Gateway runtime profiles and interactive setup

A runtime profile is optional because tools differ. It provides a package-managed state location readable only by the Gateway identity, the package broker, and the interactive owner where that is needed for setup. It is not a copy of the desktop profile.

For a tool with a supported configurable file-backed credential store, the Hub requests `startInteractiveSetup`. The broker launches the registered tool in the interactive desktop session with the profile's approved environment. The user follows the tool's official setup flow locally, including browser OAuth. The Gateway then verifies the same registered executable and profile from the isolated session.

For Gog, this supports its documented `GOG_HOME`, file-keyring, and keyring-password configuration. The broker keeps any generated keyring password in protected local storage and passes it only to the relevant desktop setup and Gateway child processes. It must never place the value in Machine PATH/environment, logs, diagnostics, chat, or a PR fixture.

A tool that only supports per-user Credential Manager/DPAPI with no supported configurable storage is reported as unsupported for cross-session credential sharing. The bridge must not copy browser data, Credential Manager secrets, OAuth callbacks, or user profiles.

## Security invariants

- The user explicitly chooses or approves every executable registration.
- The Hub cannot directly alter Gateway PATH, ACLs, shims, or scheduled lifecycle state.
- The broker canonicalizes paths, rejects scripts/directories/disallowed locations, and prevents shim hijacking.
- Gateway receives only package-generated shims, not the desktop user's complete PATH.
- ACLs use the minimum identities and permissions required. `Everyone:F` is prohibited.
- Credential material, OAuth client JSON, callback URIs, authorization codes, tokens, account identity, and tool output are excluded from model/chat state and diagnostics.
- A tool is `Gateway-ready` only after an isolated-session verification succeeds.

## State model

```text
Discovered -> Selected -> Registered -> Gateway verification pending
  -> Gateway-ready -> Runtime profile created -> Authentication required
  -> Authentication pending -> Authentication verified
```

Executable availability and authentication are distinct states. Installing a binary in either desktop or Gateway Tools location does not imply that credentials are available to Gateway.

## Non-goals

- Downloading, installing, updating, bundling, or uninstalling third-party tools.
- Provider-specific `clawctl` commands.
- Importing desktop PATH, user profiles, Credential Manager, browser profiles, or token stores.
- Reimplementing skill/tool authentication protocols.

## Temporary boundary

This package-side bridge is potentially temporary. A future upstream OpenClaw runtime may give skills, plugins, and tools a first-class agent-session model and a supported interactive-login contract. Until that exists, this package provides the narrow Windows/MXC bridge needed to preserve the isolation boundary while making user-installed tools usable.