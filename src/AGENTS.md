# Source implementation guide

Read the repository-root `AGENTS.md` before this file. This scope owns the three .NET projects under `src\`; keep their boundaries explicit and move shared contracts only through a complete caller cutover.

## Project owners

- `OpenClaw.Launcher` owns the packaged entrypoint, `clawctl` command tree and output, setup state, MXC coordination, gateway lifecycle, diagnostics, and host-side session execution.
- `OpenClaw.SessionHost` owns work inside the isolated session under the agent identity: runtime installation, config inspection, process launch/supervision, collection, and termination.
- `OpenClaw.SessionProtocol` owns the versioned AOT-safe JSON file contract between launcher and session host. Change both producers and consumers together; reject unsupported versions rather than guessing compatibility.

## Invariants

- `openclaw` arguments belong to upstream. Forward the original vector unchanged and keep System.CommandLine scoped to `clawctl`.
- Sessions are mandatory. `clawctl setup` provisions and records one; `openclaw` provisions only when its setup marker is absent, then starts the recorded session. Unreadable, incomplete, foreign, newer-schema, preparing, tearing-down, and other degraded state remains an explicit `clawctl setup` or `clawctl teardown` recovery path.
- Keep packaged `app\openclaw.mjs` immutable and execute it from the package inside the session. Install Node.js in the agent profile and prepend that runtime to the agent process path. Mirror only packages discovered to carry `.node`, `.dll`, or `.exe` artifacts into agent LocalState, preserving each owning package directory; never hard-code the package set.
- The launch request names the staged native root and places the preload on the agent Node.js argument vector so OpenClaw's reconstructed agent CLI retains it. The preload appends itself to the agent account's `NODE_OPTIONS` for ordinary child processes; invoking-host values must not leak into the guest.
- Every launch using a staged native root holds it for its entire lifetime. Leave a held superseded root intact for a later setup to reclaim; a successful rename or delete is not proof that no process is using it.
- Stage the session helper into the shared workspace during setup; the agent cannot execute it in place from another package identity's WindowsApps directory.
- Preserve caller working directory and package-qualified entrypoint resolution. Unrecognized entrypoint names fall back only as documented and tested.
- Use contract-specific source-generated `JsonSerializerContext` metadata for every serialized production shape. Do not use reflection-based serialization or APIs that are unsafe under trimming.
- Human `clawctl` output uses Spectre.Console renderables. Build dynamic text with renderable APIs rather than interpolated markup; keep JSON output, exit codes, color policy, and redirected output behavior separate.
- Gateway status reports observed state and the observed listening port. An absent configured port stays absent so upstream configuration can own it.
- Diagnostics redact included text, exclude credential databases and auth profiles, and serialize concurrent appends.

## Proof

- Run the quality gate and relevant xUnit tests for managed changes.
- Run `scripts\Test-NativeAotCli.Tests.ps1` for CLI, startup, entrypoint, JSON, reflection, interop, trimming, or other NativeAOT-sensitive changes.
- A protocol change proves launcher and session-host round trips plus unsupported-version rejection. A gateway change proves setup/status/start/stop behavior without touching real scheduled tasks or package state.
