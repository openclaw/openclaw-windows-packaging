---
name: crabbox
description: >-
  Run this repository's configured Windows Hyper-V Crabbox jobs, preview their
  sync scope, reuse or release an owned lease, and report validation evidence.
  Use for disposable local Windows checks and same-lease iteration. Not for
  preparing VHDX templates, cloud providers, Actions hydration, or package production.
license: MIT
---

# Crabbox

This project skill builds on the official Crabbox v0.81.0 `init --detect`
scaffold. Keep execution logic in the
[repository config](../../../.crabbox.yaml) and existing test scripts; use the
[local Windows guide](../../../docs/crabbox-local-windows.md) for commands and
prerequisites. Discovery is guidance, not permission to provision a VM.

## Operating contract

| Concern | Contract |
| --- | --- |
| Use when | Running or inspecting this repository's local Windows Hyper-V checks, same-lease resync, or exact owned-lease cleanup. |
| Do not use when | ISO conversion ([windows-iso-to-vhdx](../windows-iso-to-vhdx/SKILL.md)), template preparation ([crabbox-hyperv-windows](../crabbox-hyperv-windows/SKILL.md)), cloud providers/hydration, or producing/installing a package. |
| Required inputs | Repository root, verified Crabbox binary/version, intended job, machine-local provider prerequisites, and the exact owned lease ID for reuse. |
| Mutation boundary | Preview read-only first. Acquire or execute only within explicit task authority; never alter unrelated VMs, templates, host configuration, or credentials. |
| Done when | The actual job result and relevant synced edits are verified, proof limits are stated, and required exact-lease cleanup is independently confirmed. |

## Inspect before execution

1. Read the config and guide from the repository root in PowerShell 7.
   Verify the selected binary/version with `--version`; inspect `config show`,
   `doctor --provider hyperv`, `sync-plan --json`, and the selected job's
   `job run --dry-run <job>` output. Put job flags before the positional job
   name; v0.81.0 does not parse trailing flags. Do not equate doctor exit 0 with passing checks.
2. Keep the image, user, switch, and guest credential in trusted user config or
   supported environment variables. Never request a password in chat, print it,
   commit it, or forward it to a job.
3. Leave `CRABBOX_CONFIG` unset for normal user/repository merging. An explicit
   file replaces the entire file search and can lose user-configured provider
   prerequisites; environment and flags still override it.
4. Confirm sync stays inside the intended worktree and inspect exclusions.
   Dirty tracked files and nonignored untracked files sync, including hidden
   `.setup` inputs. Exclusions are not a secret scanner.

## Run and reuse

Use the [timing reference](../../../docs/crabbox-timings.md)
to budget work: one observed native acquisition took 7m 42s; explicit warm
transport checks took 57s/81s, excluding acquisition and separate stop. Those
are samples, not promises or .NET/build timings. Reuse avoids VM creation,
not the configured sync or tool checks; do not treat a timeout as an estimate.

- `transport-smoke` runs the existing Node test entry. Crabbox bootstraps
  Node/npm; this job does not qualify .NET, PowerShell, or package behavior.
- `quality` uses the canonical PowerShell quality script; `tests` runs the
  managed solution tests. Require the guide's PowerShell 7/.NET toolchain
  prerequisites first. Do not install tools or substitute smoke for these lanes.
- For an authorized one-shot check, use `job run <job>` and check its exit
  code. `stop: auto` releases a newly acquired lease after success or failure.
- Warm a reusable runner only when task authority permits it and reuse is
  useful: `warmup --provider hyperv --target windows --keep`. Reuse the returned
  slug interactively and retain the stable `cbx_` ID in scripts/evidence.
- Run `job run --id <owned-id> <job>` after each relevant local edit. Explicit
  reuse remains running under `stop: auto`; do not acquire another VM merely
  to avoid recording the current lease.
- Use `status --id <owned-id> --wait` when readiness evidence is needed.
  Inspect a failing runner only within the authorized workload. Do not diagnose
  a product failure from a reused workspace that fails sync sanity; stop the
  exact owned lease and obtain authorization before replacing it.
- Use `--no-sync` only for an intentional readback of that verified workspace,
  not to imply local edits reached the guest. Do not add a cache-volume or
  hydration path outside the selected provider's supported contract.

## Finish and report

Record the binary version, source/head and dirty inputs, exact command/job,
exit code, test outcome, and meaningful resync evidence. Distinguish host-side
checks from guest execution; never infer .NET/quality success from Node smoke.

Release a reusable lease with `stop --provider hyperv <owned-id>` in `finally`.
Check release errors and independently confirm the exact owned VM,
differencing disk, and claim are gone; preserve the ID and recovery diagnostic
if cleanup fails. Never use blanket cleanup or change the immutable template.
Report every relevant unrun lane and any intentionally retained lease.

## Source freshness

The scaffold and workflow were verified with
[Crabbox v0.81.0 init](https://github.com/openclaw/crabbox/blob/v0.81.0/internal/cli/init.go).
Before using another version, recheck its help and provider/job/config contracts
against the guide. Do not import generic Linux/cloud defaults or rerun `init
--force` over repository customizations.
