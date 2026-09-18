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
machine agents nor disturbs a gateway record for any other session.
`clawctl teardown --force` explicitly confirms deletion of the recorded owned
session, its data, and local setup state; it does not uninstall the package.

`clawctl setup --fresh` explicitly authorizes a reset of this installation.
It captures a redacted pre-reset report, performs the normal owned teardown,
then clears only the contents of the package-owned state roots before running
the ordinary setup route. It refuses unpackaged execution, never follows
reparse points, and stops without a wipe or replacement setup when teardown is
incomplete. It does not onboard OpenClaw or start the gateway.

`openclaw` always runs inside the isolated session recorded by `clawctl setup`.
There is no environment variable, option, or automatic fallback that runs
OpenClaw on the host. Before touching any recorded state, both `clawctl setup`
and `openclaw` require that this machine can host a session: an MXC runtime
that is unavailable, a backend that reports no isolation-session support, a
host build measured as unsupported, or a missing package identity all fail with
the same message, which names Windows Update or a newer Windows version as the
remedy and prints the diagnostic log path. Support that cannot be determined
(the host build is unreadable and the backend probe failed) is not a refusal:
provisioning is attempted so the backend's own error surfaces.
`clawctl status` preserves the recorded ownership record and does not provision
or replace a session. It is not passive: `ProbeRecordedStatusAsync` invokes the
backend `StartAsync` operation for the recorded provision as its status probe.

## Agent runtime and helper

The package application tree is immutable and runs directly from the MSIX; the
launcher does not extract, copy, hash, or repair it at runtime. Setup is the
only route that installs a Node.js runtime: `RunSetupCoreAsync` asks the guest
helper to install the runtime under the agent's own profile. The invoking
user's host runtime is never prepared, because nothing runs on the host.

The agent cannot execute the package's WindowsApps helper binary directly.
During setup, the launcher stages only `openclaw-session-host.exe` into the
shared session workspace. It does not stage the application tree. The helper
has distinct modes for launching a request, inspecting processes/listeners,
installing the agent runtime, and collecting requested diagnostics.

The helper also exposes an internal `--check-config <request-path>` mode used by
the launcher after packaged `openclaw` calls. It resolves the agent account's default
`.openclaw\openclaw.json` and classifies the file as `Absent`, `NotReady`, or
`StartupEligible` without starting Node.js or OpenClaw. This is deliberately a
file-only heuristic for the default OpenClaw-generated config:
`StartupEligible` means only that the file can be parsed and contains
`gateway.mode` set exactly to `local`. It does not resolve alternate profiles,
includes, environment substitution, secrets, plugins, bind/auth policy, ports,
or any other runtime dependency, and therefore does not claim that a gateway
will start or remain healthy.

On the local win-x64 NativeAOT publish, ten direct helper invocations took
53.5-108.9 ms (59.15 ms median), including process startup and file inspection
but excluding the MXC round trip. No invocation started Node.js or OpenClaw.

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

Both `clawctl status` and `clawctl gateway-service status` add the helper's
file-only config readiness whenever the managed gateway is not confirmed
running. The gateway-only command first observes gateway state, then starts
only an already-recorded session when necessary to reach the helper; it never
provisions a replacement or starts the gateway. Human output reports
`not configured`, `not ready`, `startup eligible`, `unavailable`, or `unknown`.
Structured output carries the same state under `gateway.readiness` with the
stable reason. A running gateway omits readiness and avoids the helper call.

After an `openclaw` child exits, the launcher uses the helper's file-only
readiness result before checking the managed gateway record and liveness. A
successful interactive call gets a start suggestion only for `NotStarted` or
`Stopped`; unsuccessful or redirected calls, and `Starting`, `Unhealthy`, or
`Unknown` gateway states, remain silent. The advisory path cannot change the
OpenClaw exit code.

The suggestion remains eligible until a manual
`clawctl gateway-service start` invocation or an observed `Running` state.
Acknowledgement is package-local and keyed to the Windows token authentication
ID, so it suppresses later checks only for the current Windows logon. The
sign-in recovery command carries a hidden provenance marker and does not count
as a manual acknowledgement; a later OpenClaw invocation that observes its
running gateway does.

Agent entrypoint startup captures and restores console state and initializes
UTF-8 just as the control entrypoint does. Postflight rendering treats
foreground interactivity and stderr's native console capability separately:
the app-alias stderr proxy can receive ANSI and the crab glyph without
supporting `GetConsoleMode`, while a native console still requires successful
VT enablement.

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

1. Run `clawctl setup` after installing or updating the package to prepare the
   isolated agent session. It fails on a machine that cannot host a session,
   naming Windows Update or a newer Windows version as the remedy.
2. Run `openclaw <arguments>` for the upstream OpenClaw CLI, or
   `clawctl pwsh` for an interactive agent shell.
3. Use `clawctl gateway-service start`, `status`, and `stop` for the managed
   gateway. Use `clawctl status` to inspect session ownership and state.
4. Run `clawctl collect-logs` when reporting a problem, then review the
   resulting ZIP before sharing it.
5. Run `clawctl teardown --force` to confirm removal of the owned isolated
   session and its data while retaining the installed package.

The `clawctl` command tree intentionally owns only these package-management
operations. Upstream commands such as `doctor`, `gateway`, and `uninstall`
remain `openclaw` arguments and are forwarded unchanged.
