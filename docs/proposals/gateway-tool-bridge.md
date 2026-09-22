# Gateway Tool Runtime Bridge design

## Status

Draft implementation design. This package owns both the authoritative runtime
bridge and its future Gateway Tools Control UI. The Control UI is a follow-up
package PR that depends on the bridge; it is not implemented in the legacy
Windows Hub.

## Problem

The packaged Gateway runs in an MXC-isolated agent session. A command available
in the interactive Windows user's `PATH`, profile, or Credential Manager is
therefore not automatically available to the Gateway. Users need to be able to
install a tool using its official distribution, then deliberately make that
existing tool available to the isolated Gateway without importing their desktop
environment.

## Scope

This package owns the authoritative tool bridge. It exposes a versioned local
broker to the package-owned Control UI Adapter and will:

- register a user-approved executable under a safe command alias;
- support a desktop-installed executable and an executable installed for the
  Gateway-agent user;
- validate the canonical executable path and command alias, prevent alias
  collisions, and record the approved registration;
- create Gateway-only command shims and prepend only their directory to
  Gateway child-process `PATH`;
- provision a managed tool profile for tools with a documented supported
  configuration/state-root override;
- launch the registered tool's normal interactive setup in the human desktop
  session with that managed profile configuration;
- verify each registration from the actual isolated Gateway session with a
  bounded, non-mutating tool-specific command;
- return bounded, redacted status and refresh Gateway only when a registration
  change requires it.

The bridge is provider- and tool-neutral. Each tool must have a reviewed
managed tool profile contract; the generic bridge does not reimplement a
provider login protocol.

## Installation sources

The registration record distinguishes executable source from managed profile
ownership:

| Source | User action | Gateway behavior |
| --- | --- | --- |
| Desktop or machine installation | Select an existing executable in Gateway Tools | Broker verifies that exact path in the isolated session and creates a shim only if it works. |
| Gateway-agent installation | Select an existing executable installed for the Gateway-agent user | Broker verifies that exact path in the isolated session and creates a shim only if it works. |

The package records the validated absolute executable path but never exposes it
to the Control UI, plugin, chat, or model context. Dynamic `PATH` resolution is
not trusted at Gateway invocation time.

## Broker contract

The launcher and session protocol define a narrow, versioned local contract.
The Control UI Adapter is authenticated and is not the policy owner.

```text
listTools
registerTool
scanTools
verifyTool
setToolEnabled
unregisterTool
createManagedProfile
startInteractiveSetup
```

A browser-safe registration summary contains only non-secret bounded state:

```json
{
  "registrationId": "toolreg_opaque",
  "command": "tool",
  "displayName": "User-installed tool",
  "executableSource": "desktop",
  "enabled": true,
  "managedProfileId": "toolprofile_opaque",
  "gatewayVerification": "ready"
}
```

Raw executable paths remain local broker state. Verification output is bounded
and redacted.

## Managed Tool Profile Contract

A tool qualifies for this bridge only when it documents a supported way to
redirect its own profile, configuration, or state location. A reviewed contract
may declare a process-scoped environment variable, command-line profile option,
or other documented tool-specific state-root override.

The package provisions persistent integration state under:

```text
C:\ProgramData\OpenClaw\GatewayTools\profiles\<opaque-profile-id>\
```

When the bridge starts the registered tool, it supplies the reviewed profile
configuration only to that approved child process. The tool writes its own
state to the managed profile rather than its normal per-user default; the
bridge does not use filesystem redirection or alter global environment
variables.

For a reviewed tool that requires a protected runtime secret for its documented
file-backed profile, the package stores only a protected secret handle and
injects the secret only into approved setup and Gateway child processes. It
must not appear in shims, registry JSON, ordinary configuration, User/Machine
environment variables, browser responses, plugin responses, logs, diagnostics,
or model context.

Tools limited to an interactive user's Credential Manager/DPAPI, browser
profile, desktop IPC, hardware binding, or another non-relocatable state model
require a separate reviewed integration or are unsupported by this bridge.

## Interactive setup and verification

After user registration, the user may select **Start setup**. The broker starts
only the validated registered executable in the human interactive desktop
session and applies its reviewed managed-profile configuration. The user then
uses the tool's normal documented setup. Any external interaction, browser
activity, callback, code, or token is owned by the tool and external service;
the bridge does not implement, inspect, proxy, or store it.

The bridge subsequently verifies the same registered executable and managed
profile from the isolated Gateway-agent identity using a fixed, bounded,
non-mutating verification policy. The Control UI receives only safe states:
`not_configured`, `pending`, `verified`, or `failed`.

## Security invariants

- The user explicitly chooses or approves every executable registration.
- The Control UI cannot directly alter Gateway `PATH`, ACLs, shims, or
  scheduled lifecycle state.
- The broker canonicalizes paths, rejects scripts/directories/disallowed
  locations, and prevents shim hijacking.
- Gateway receives only package-generated shims, not the desktop user's
  complete `PATH`.
- ACLs use the minimum identities and permissions required. `Everyone:F` is
  prohibited.
- Credential material, external-service setup data, callback data, tokens,
  account identity, raw executable paths, and raw tool output are excluded from
  UI/plugin/model/chat state and diagnostics.
- A tool is Gateway-ready only after isolated-session verification succeeds.

## Non-goals

- Downloading, installing, updating, bundling, or uninstalling third-party
  tools.
- Changing User or Machine `PATH`.
- Importing desktop profiles, Credential Manager entries, browser profiles, or
  token stores.
- Arbitrary command execution or arbitrary environment injection.
- Reimplementing skill/tool authentication protocols.
- Allowing the isolated Gateway plugin direct access to the desktop-user
  broker.

## Temporary boundary

This package-side bridge is potentially temporary. A future upstream OpenClaw
runtime may give skills, plugins, and tools a first-class agent-session model
and a supported interactive setup contract. Until that exists, this package
provides the narrow Windows/MXC bridge needed to preserve the isolation boundary
while making user-installed tools usable.
