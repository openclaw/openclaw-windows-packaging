# Handoff: MXC session integration

Working notes for resuming this branch on another machine. **Transient** — delete
before the branch is opened for review.

Branch `feat/mxc-runtime-backfill` @ `cfac73f`, based on `origin/main` @ `6f89905`.
Pushed; nothing else is. No PR exists yet.

```powershell
git clone https://github.com/openclaw/openclaw-windows-packaging.git
cd openclaw-windows-packaging
git checkout feat/mxc-runtime-backfill
```

## What this adds

Runs OpenClaw inside a Windows IsolationSession (MXC) instead of directly on the
host, and manages a background gateway in that session.

| | |
|---|---|
| `src\OpenClaw.Launcher\Mxc` | Project-owned MXC contracts + temporary transport over the pinned `@microsoft/mxc-sdk` CLI |
| `src\OpenClaw.Launcher\Session` | Session ownership record, lifecycle coordinator, execution |
| `src\OpenClaw.Launcher\Gateway` | Gateway record, controller, Task Scheduler persistence, diagnostics |
| `src\OpenClaw.SessionHost` | NativeAOT guest helper (launch / supervise / inspect / stop) |
| `src\OpenClaw.SessionProtocol` | Launch + inspect contracts shared by both executables |

Commands: `clawctl session <status\|stop\|remove>` and
`clawctl gateway-service <install\|status\|start\|stop\|uninstall\|diagnose>`.

Design docs live in `docs\`: `mxc-runtime.md`, `session-host.md`,
`session-state.md`, `session-routing.md`, `gateway-service.md`, and
`mxc-compatibility-evidence.md` (the measured backend evidence — read this one
before changing anything about the command line or ownership checks).

## Prerequisites

PowerShell 7, the .NET SDK pinned in `global.json`, and Visual Studio Build
Tools with **Desktop development with C++** plus the Windows SDK for the
NativeAOT and MSIX paths.

## Validation

All four pass at `cfac73f`. Run them from the repository root.

```powershell
.\scripts\Test-DotNetQuality.ps1                                    # analysis + format + style
dotnet test .\OpenClaw.Gateway.MSIX.slnx --configuration Release    # 475 tests
.\scripts\Test-NativeAotCli.Tests.ps1                               # 12 published-binary scenarios
.\scripts\Test-SigningInputs.Tests.ps1
```

`Test-NativeAotCli.Tests.ps1` needs the VS installer directory on `PATH`:

```powershell
$env:Path = (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) `
  'Microsoft Visual Studio\Installer') + ";$env:Path"
```

The xUnit suite runs under JIT, so anything touching interop, JSON, or trimming
needs the NativeAOT publish as well. A clean `dotnet build` is not evidence.

## Where the work stopped

Four slices are complete and committed: MXC readiness, session execution,
gateway management, and the rebase onto upstream's System.CommandLine parser.

**In progress: `mxc-explicit-setup`** — nothing written yet, so there is no
half-finished code to reconcile. The approved redesign changes the lifecycle
from implicit to explicit:

| Command | Behavior |
|---|---|
| `openclaw` before setup | Fails, naming `clawctl setup`. No provisioning, no host fallback. |
| `clawctl setup` | Provisions/reuses the session, persists launch configuration, **enables** gateway startup at sign-in — but does **not** start the gateway. |
| `openclaw [...]` after setup | Transparent passthrough into the owned session. |
| `clawctl status` | Aggregate: session, startup registration, gateway — each reported independently. |
| `clawctl teardown [--force]` | Stops the gateway, removes owned startup, then MXC `Stop` → `Deprovision`. Prompts unless `--force`; `--force` skips confirmation only, never ownership checks. |

Then: `mxc-lifecycle-status`, `mxc-explicit-teardown`, `mxc-lifecycle-validation`,
`mxc-real-machine-evidence`, `mxc-draft-publication`. `mxc-public-sdk-gate` stays
blocked until the MXC .NET SDK ships.

This supersedes the current `session` command group and read-only `setup`; both
still exist on the branch and are removed by the lifecycle slices.

## Constraints that produced the current design

- **`openclaw` forwards arguments verbatim.** No host flags, no consuming `--`.
  Management lives under `clawctl`. Tests protect this.
- **`gateway-service`, never `gateway`** — OpenClaw owns `openclaw gateway run`.
- **Ownership is recorded, never inferred.** MXC `deprovision` is idempotent and
  unrelated agent accounts already exist on machines, so neither the absence of
  an error nor the presence of an account proves anything.
- **A record is not liveness.** Stopping the sandbox kills detached work
  silently, so a gateway record routinely outlives its process. Every "running"
  claim is re-established inside the session: live process **and** matching
  creation time **and** a listener owned by that process or a descendant.
- **"Unknown" is never rendered as "stopped."** They call for opposite actions.
- **No silent host fallback.** A session that was required and cannot be
  provided must fail loudly.

## Defects found by running real binaries

Each was invisible to the managed test suite and is worth knowing before
touching the relevant area.

1. **`cmd.exe` discards the outer quote pair.** The backend dispatches through
   `cmd /c`, which strips the first and last quote when a command line holds
   more than two. A naturally quoted command has four, so the helper path
   arrived unquoted and died at the space in `Program Files`. Fixed by adding a
   deliberate outer pair. The old test asserted the composed string and passed
   against the broken command line; it is replaced by tests that dispatch
   through the real command processor (7 of 9 fail without the fix).
2. **Task Scheduler rewrites identity.** A logon trigger registered with a SID
   reads back as an account name, so text comparison reported drift forever.
   Account identity is compared by resolved SID.
3. **Orphaned gateway.** Killing the recorded process left the gateway running
   and holding its port, owned by nothing. The supervised process is now in a
   kill-on-job-close job.
4. **`diagnose` refused to run when broken** — the one command you need when the
   stack cannot be assembled. It now reports the broken first link.

## Unverified

- **Nothing has run inside a real MXC session.** Every live probe drove the
  guest helper directly on the host. This is `mxc-real-machine-evidence` (gate
  G4), and it needs a machine whose state you are willing to mutate.
- Terminal Ctrl+C and resize behavior through attached execution.
- PFN-specific activation resolving away from a similarly named package.
- ARM64 publishes cleanly but has not been executed.

## Notes

- `backup/pre-rebase-18257f1` is pushed and holds the pre-rebase tip. The rebase
  accounting was verified file-by-file; only `ClawCtlCommand.cs` is absent,
  because upstream deleted it when it adopted System.CommandLine. Delete the
  backup ref once this work merges.
- Upstream turned on `TreatWarningsAsErrors` with `AnalysisMode=All`. New
  warnings fail the build. Test-only relaxations are documented in
  `.editorconfig` under `[tests/**/*.cs]`.
- `scripts\Get-MxcRuntime.ps1 -Architecture x64` stages the pinned MXC runtime.
  It is only needed for packaging builds, not for `dotnet build` or tests.
