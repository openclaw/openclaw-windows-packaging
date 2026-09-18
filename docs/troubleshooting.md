# Troubleshooting

This page helps contributors diagnose the packaged Windows experience from
what they observe. It does not own the command reference, installation
requirements, release process, architecture, or local-development workflow;
see the [`clawctl` command table](../README.md#clawctl) for those.

## `clawctl setup` says Windows cannot host isolated sessions

**Check.** Read the complete message from `clawctl setup`. A refusal identifies
the missing package identity, unavailable isolated-session runtime, unavailable
backend, or an unsupported Windows build.

**Likely cause.** This package has one execution path: an isolated agent
session. It does not silently run OpenClaw on the host when the session backend
is unsupported. When support cannot be determined, setup attempts provisioning
so that the backend can report its own error rather than rejecting the machine
from incomplete evidence.

**Fix.** If the message says the Windows build is unsupported, install the
latest Windows updates from **Settings > Windows Update**, or move to a newer
Windows version, then run:

```powershell
clawctl setup
```

For another stated prerequisite, correct that prerequisite and rerun setup.

## `openclaw` says setup is required, incomplete, or its setup marker is unusable

**Check.** Preserve the exact setup error. It distinguishes a missing,
unreadable, incompatible, incomplete, or foreign-package setup marker from a
missing recorded session.

**Likely cause.** Running `openclaw` requires both the explicit, package-local
setup marker and the matching recorded isolated session. A session created by
an older management operation is not authorization to run OpenClaw, and the
runtime deliberately does not turn a stale marker into provisioning.

**Fix.** Run:

```powershell
clawctl setup
```

If the message instead says teardown is incomplete, finish it with
`clawctl teardown` before setting up again. Do not expect `openclaw`, status,
or a gateway command to provision a replacement session implicitly.

## The session is unavailable or will not start

**Check.** Run:

```powershell
clawctl status
```

The status command reports the recorded session and may start/probe that
already-recorded session to obtain diagnostics. It is not a passive probe, but
it never provisions a replacement or starts the gateway. Preserve its session
state and any MXC diagnostic detail.

**Likely cause.** The package can no longer reach its recorded isolated
session, the MXC runtime/backend is unavailable, or the recorded session is no
longer usable.

**Fix.** Resolve the reported MXC or session error, then run `clawctl setup` to
repair or reuse the package-owned session. Do not create or adopt an unrelated
agent account or session.

## The gateway is not started, not listening, or is unhealthy

**Check.** Inspect only the gateway first:

```powershell
clawctl gateway-service status
```

`gateway-service` is the command name. Its status command does not start a
gateway; when needed, it may start/probe the already-recorded session only to
report file-only configuration readiness. Check the reported gateway state,
the scheduled-task recovery state, and the log tail emitted for stopped or
unhealthy gateways.

**Likely cause.** `not started` means this installation has not started a
gateway. `stopped` can be an ordinary consequence of stopping the isolated
session. `unhealthy` means the recorded process is alive but is not serving;
`unknown` means liveness could not be established, so the launcher will not
risk starting a second gateway.

**Fix.** For `not started`, first make the configuration ready (next symptom),
then run:

```powershell
clawctl gateway-service start
```

For an unhealthy gateway, inspect the emitted log tail or run
`clawctl gateway-service stop` before attempting another start. Use the port
reported by status as the observed endpoint. Do not assume the upstream
default port (18789): an explicit OpenClaw `gateway.port` can differ, and
multiple unclassified listeners intentionally do not identify an endpoint.

## Status says the default configuration is missing or not ready

**Check.** Look at the readiness state and reason from `clawctl status` or
`clawctl gateway-service status`. The check reads only the agent profile's
`.openclaw\openclaw.json`; it does not start the gateway or edit configuration.

**Likely cause.** `absent` / `config-file-missing` means that file is missing.
`not ready` can mean it cannot be read, is invalid JSON, lacks `gateway`, lacks
`gateway.mode`, or has a mode other than the exact value `local`.

**Fix.** Create or repair the default OpenClaw configuration so it has a
readable object with:

```json
{
  "gateway": {
    "mode": "local"
  }
}
```

Comments and trailing commas are accepted, but `LOCAL`, `remote`, and a
non-string mode are not ready. Re-run the status command, then start the
gateway only after readiness reports ready.

### Example status output

The exact fields vary with the package and machine, but this output shows the
important distinction between a running session and an unconfigured gateway:

```text
clawctl status

  Session:      [ok] running
  Runtime:      Node.js 24.20.0
  Gateway:      not started
  Readiness:    not configured
                the default config file is missing
  Recovery:     [ok] configured
```

The corresponding `status --json` response uses `schemaVersion` 1 and reports session
`running`, gateway `not-started`, readiness `absent` with reason
`config-file-missing`, and recovery `configured`. Machine-specific identifiers
are intentionally omitted.

## A command was cancelled with Ctrl+C

**Check.** If cancellation or Ctrl+C produced exit code `130`, treat it as an
interrupt rather than a failed OpenClaw launch.

**Likely cause.** The session executor normalizes the Windows Ctrl+C exit code
to portable exit code 130 when no control result can be read.

**Fix.** No recovery is required for an intentional interrupt. Rerun the
command when ready; collect diagnostics only if the interruption was
unintentional or the command cannot complete without it.

## Package registration fails inside a special-profile session

**Check.** Confirm that registration was attempted from a special-profile
isolated session and retain the AppX policy error.

**Likely cause.** AppX deployment in that profile requires the
`AllowDeploymentInSpecialProfiles` policy.

**Fix.** Have the device administrator enable the required AppX policy, then
retry registration. This repository does not prescribe a registry command for
that policy; use the organization's managed-policy process.

## Collecting diagnostics for escalation

**Check.** Run:

```powershell
clawctl collect-logs [--output <path>]
```

The command prints the resulting ZIP path on its own line. Without `--output`,
it creates the ZIP in package state. It is best effort: if the agent session
cannot be reached, the ZIP still contains host diagnostics and records that
agent-side collection failed.

**Likely cause.** Partial setup, session reachability problems, and gateway
failures can prevent collection of some agent files without preventing
collection of the host evidence needed to diagnose them.

**Fix.** Review the ZIP before sharing it. The collector includes selected
OpenClaw logs and `.openclaw\openclaw.json*` candidates, applies
credential-shaped-value redaction to JSON, and intentionally excludes SQLite
databases and authentication-profile files. Redaction is not a substitute for
review, and the collector does not enumerate arbitrary agent-profile files.

## Bug report collection checklist

Attach the reviewed diagnostics ZIP, the exact command and complete output,
the `clawctl status` (or `status --json`) result with machine-specific IDs
removed, Windows version/build, package version, and the observed session,
gateway, readiness, recovery, and scheduled-task states. For a special-profile
registration failure, include the policy error.

## Source authorities

The behavior above is verified against:

- `src\OpenClaw.Launcher\Session\SessionSupportPolicy.cs` and
  `tests\OpenClaw.Launcher.Tests\Session\SessionSupportPolicyTests.cs`
- `src\OpenClaw.Launcher\Session\SetupStateStore.cs`,
  `SessionRuntime.cs`, and their session tests
- `src\OpenClaw.Launcher\Session\SessionExecutor.cs` and
  `SessionExecutorTests.cs`
- `src\OpenClaw.SessionHost\SessionConfigReadinessChecker.cs` and
  `SessionConfigReadinessCheckerTests.cs`
- gateway status, address, persistence, and diagnostics implementations and
  tests under `src\OpenClaw.Launcher\Gateway` and
  `tests\OpenClaw.Launcher.Tests\Gateway`
- [`mxc-compatibility-evidence.md`](mxc-compatibility-evidence.md)
