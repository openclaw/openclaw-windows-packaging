# MXC session runtime and operational evidence

This document describes the packaged isolated-session implementation and the
observable guarantees it makes. It does not describe an external MXC protocol
as a stable public API.

## Runtime boundary and MXC seam

The launcher owns a small session contract in
`OpenClaw.Launcher\Mxc\IMxcSessionClient`. `MxcCliSessionClient` is the current
implementation: it invokes the pinned `@microsoft/mxc-sdk` CLI and confines
its preview wire details to `MxcWireProtocol` and `MxcWireModels`. The rest of
the launcher depends on the project-owned contract, so the CLI transport can
be replaced by the official .NET SDK without changing lifecycle, routing, or
gateway callers.

`OpenClaw.SessionProtocol` is separate from that backend seam. It is the
versioned, launcher-to-guest request/result contract used for execution,
inspection, runtime installation, and collection. A backend execution request
uses a controlled `cmd.exe` command line only to start the staged helper; the
requested executable and argument vector are JSON data rather than interpolated
into that command line.

## Session ownership and routing

`clawctl setup` is the required lifecycle entry point. It writes package-local
setup state and records the session that this installation owns in
`session.json`. Ownership is never inferred from a machine account or profile:
unrelated agent accounts may exist, and teardown must remain safe and
idempotent. When MXC reports that this recorded provision is missing, setup
reprovisions a replacement for this installation and removes only a gateway
record that names that explicitly stale session; it neither adopts unrelated
machine agents nor disturbs a gateway record for any other session. `clawctl teardown [--force]` removes
only the recorded owned session and local setup state; it does not uninstall
the package.

`clawctl setup --fresh` explicitly authorizes a reset of this installation.
It captures a redacted pre-reset report, performs the normal owned teardown,
then clears only the contents of the package-owned state roots before running
the ordinary setup route. It refuses unpackaged execution, never follows
reparse points, and stops without a wipe or replacement setup when teardown is
incomplete. It does not onboard OpenClaw or start the gateway.

`openclaw` uses `OPENCLAW_SESSION` to select a route:

| Value | Result |
|---|---|
| Unset | Use the isolated session when the MXC backend reports support; otherwise run directly on the host. |
| `0`, `false`, `off`, or `no` | Run directly on the host. |
| `1`, `true`, `on`, or `yes` | Require the isolated session. A session failure is reported; the launcher does not silently fall back to the host. |

Unrecognized values are rejected rather than interpreted as disabled.
`clawctl status` preserves the recorded ownership record and does not provision
or replace a session. It is not passive: `ProbeRecordedStatusAsync` invokes the
backend `StartAsync` operation for the recorded provision as its status probe.

## Agent runtime and helper

The package application tree is immutable and runs directly from the MSIX; the
launcher does not extract, copy, hash, or repair it at runtime. Direct host
execution calls `NodeRuntimeResolver.Resolve` and requires the bundled Node.js
runtime to have already been extracted into the invoking user's LocalState.
Normal isolated-session setup does not prepare that host runtime:
`RunSetupCoreAsync` asks the guest helper to install the runtime under the
agent's own profile. To prepare the host runtime, use
`clawctl setup --no-isolation`; it first requires the isolated-session support
check and then runs the session-free host setup.

The agent cannot execute the package's WindowsApps helper binary directly.
During setup, the launcher stages only `openclaw-session-host.exe` into the
shared session workspace. It does not stage the application tree. The helper
has distinct modes for launching a request, inspecting processes/listeners,
installing the agent runtime, and collecting requested diagnostics.

Opening the agent shell with `clawctl pwsh` installs an ASCII `openclaw.cmd`
shim in the shared workspace: `RunPowerShellAsync` calls `InstallToolsAsync`.
Setup alone does not guarantee that shim exists. The shim reads its Node.js and
entry-point paths from environment variables rather than embedding profile
paths, which avoids batch-file code-page corruption.
The agent's persistent user PATH is prefixed with its bundled Node.js runtime
for independently started processes; each helper launch also supplies a
request-level PATH prefix. Together these ensure the agent resolves its own
bundled `node`, `npm`, and `npx`, not a device-installed Node.js.

`clawctl pwsh` starts an interactive shell in the owned session. Its
environment is built using the interactive runtime policy so terminal-related
variables are retained when appropriate; redirected output does not synthesize
interactive defaults.

## Gateway lifecycle and recovery

`clawctl gateway-service start` starts the gateway inside the owned session;
`status` observes it without starting it; and `stop` stops it while leaving the
session and agent data intact. All require the setup record where appropriate.
The launcher does not impose an invented port: the OpenClaw configuration and
upstream default choose it unless configuration explicitly supplies one.

Setup configures sign-in recovery for the managed gateway but does not start a
gateway. Recovery is reconciled against the package identity and records its
own failures for status reporting. It is not evidence that a gateway is
currently healthy.

A stored gateway record is not liveness proof. Health requires successful guest
inspection, a live process with the recorded creation time, and a listener
owned by that process or one of its descendants. A missing record is
`NotStarted`; a stale record is `Stopped`; a live but non-serving recorded
process is `Unhealthy`; and failed inspection is `Unknown` to avoid starting a
second gateway beside one that could still be healthy.

## Diagnostics and safe collection

`clawctl collect-logs [--output <path>]` creates a ZIP at the supplied path or
in package state by default. It collects host diagnostics and, when the owned
session can be reached, stages selected agent diagnostics through the guest
helper. A session collection failure is a warning rather than a reason to
discard available host diagnostics.

Agent paths are relative to the agent profile: OpenClaw logs come from
`AppData\Local\Temp\openclaw`, and configuration candidates are
`.openclaw\openclaw.json*`. The collector excludes SQLite databases and
authentication-profile files by name. JSON entries are passed through
credential-shaped-value redaction before being added. Collection is
best-effort, and the ZIP manifest warns that redaction is not a substitute for
review before sharing.

The helper accepts only host-named sources and writes collected files to the
shared workspace. Missing sources are reported as entries rather than treated
as a collection failure, which makes a bundle useful for partial or failed
setup. The collector does not enumerate arbitrary agent-profile files.

## Supported operational flow

1. Run `clawctl setup` after installing or updating the package.
2. Run `openclaw <arguments>` for the upstream OpenClaw CLI, or
   `clawctl pwsh` for an interactive agent shell.
3. Use `clawctl gateway-service start`, `status`, and `stop` for the managed
   gateway. Use `clawctl status` to inspect session ownership and state.
4. Run `clawctl collect-logs` when reporting a problem, then review the
   resulting ZIP before sharing it.
5. Run `clawctl teardown` to remove the owned isolated session while retaining
   the installed package.

The `clawctl` command tree intentionally owns only these package-management
operations. Upstream commands such as `doctor`, `gateway`, and `uninstall`
remain `openclaw` arguments and are forwarded unchanged.
