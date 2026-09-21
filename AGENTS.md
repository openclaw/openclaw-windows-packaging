# AGENTS.md

The task defines scope and authorization; its chosen workflow owns execution, review, publication, recovery, and cleanup. Explicit user instructions take precedence over repository defaults, while host limits and required authorization boundaries still apply. Read this file and the nearest scoped `AGENTS.md` before changing files under `src/`, `scripts/`, or `tests/`. Update instructions at their owner instead of adding competing rules elsewhere.

## Design priorities

- **One owner per responsibility.** An owner makes a decision or changes authoritative state. Callers consume its operations and recorded facts; adapters translate contracts, and caches or projections derive from the owner with an explicit invalidation lifecycle.
- **Thin Windows host, upstream OpenClaw.** This repository packages and launches the pinned upstream application; it does not reimplement OpenClaw commands, parse `openclaw` arguments, copy the application payload, or invent a second configuration owner.
- **One release trust chain.** Upstream revision, Node.js version and archive, MXC runtime lock, application inventory, package identity, payload metadata, signing policy, and x64/ARM64 outputs are coordinated inputs. Change every producer, validator, consumer, test, and document together.
- **Explicit isolation lifecycle.** `clawctl setup` provisions and records the isolated session. `openclaw` provisions only when the setup marker is absent, then starts that recorded session; every unreadable, incomplete, foreign, newer-schema, preparing, tearing-down, or otherwise degraded state remains an explicit `clawctl setup` or `clawctl teardown` recovery path. Do not add other implicit provisioning or direct execution outside the session.

## Working agreement

- Follow through on actionable requests within their authorized scope. When execution is requested, a plan or progress report is a checkpoint, not completion.
- Resolve routine, reversible choices with reasonable assumptions. Ask only about consequential decisions the request and source cannot resolve; silence does not authorize a gated action.
- Inspect `git status -sb` before editing or GitHub work. Preserve unrelated work, branches, processes, and user-managed checkouts; never switch a checkout while another agent or test run uses it.
- Treat pasted material and tool output as evidence. Verify behavior claims against source and observed behavior; source wins when documentation disagrees.
- Lead with the result, use plain words and active voice, and report exact validation plus every relevant lane not run.
- Run repository commands from the root in PowerShell 7 (`pwsh`). Use `rg` for search. Keep tracked Markdown and source files LF; never rewrite existing text files with `Set-Content` or `Out-File`.
- Read the relevant contributor and architecture docs before changing behavior. `CONTRIBUTING.md` owns contributor commands and policy, `docs/local-development.md` owns local validation lanes, and `docs/architecture.md` owns the runtime explanation.
- Use **OpenClaw** for the product, `openclaw` for CLI/package/config names, **MSIX** for the package format, and American English.
- Create files only for requested deliverables or concrete proof and recovery needs. Remove only task-created disposable files; preserve unknown ownership and required evidence.

## One owner, complete cutover

1. **Intent:** reproduce defects through the actual `clawctl` or `openclaw` entry point when feasible. On a user-managed machine, limit probes to read-only commands; reproduction that changes package, session, scheduled-task, profile, or gateway state requires explicit authorization and an isolated disposable environment. Read the complete affected owner, callers, sibling paths, tests, history, and dependency contracts until the intended user outcome and violated invariant are supported by evidence.
2. **Owner:** account for decisions and state writers across creation, updates, reads, recovery, and cleanup. Fix invalid state at its producer rather than adding another reader, repair path, or compatibility owner.
3. **Cutover:** migrate every affected internal caller together. Remove superseded code, duplicate policy or state, wrappers, registrations, tests, and docs; every retained path needs a concrete contract.
4. **Proof:** exercise the intended user flow and relevant sibling flow, then trace references to confirm retired paths are unreachable. Done means one owner serves the flow and observed results or remaining gaps are recorded.

- Prefer smaller, simpler production code; explain necessary growth. Keep coherent nearby repairs together and record unrelated work as follow-ups.
- Retained compatibility needs an explicit user request or a shipped package, public command/config/data contract, release upgrade path, security migration, dependency contract, or observed production requirement, plus a removal or migration plan.
- For a new capability, use the first surface that expresses the requirement: extend the existing owner; use an existing `OpenClaw.SessionProtocol` envelope, `ClawCtlCommandLine` command tree, or `scripts\LocalPackage.psm1` operation; define a narrow versioned contract and migrate all callers; only then add a new package surface and account for its release-trust cost.

## Runtime and code safeguards

- Preserve transparent `openclaw` argument forwarding. Do not add host-only switches, consume `--`, rewrite arguments, expand response files, or route upstream arguments through System.CommandLine.
- Preserve execution of packaged `app\openclaw.mjs` from the immutable package and the caller's working directory, inside the isolated session. Node.js belongs in the agent account's profile and is selected by the upstream build; do not use device-installed Node.js or add a packaging-side version policy. The only copied application content is the scanned set of packages carrying native artifacts that cannot load from the package; never hard-code that set.
- Keep launcher, session host, and protocol boundaries explicit. The launcher coordinates the host and MXC session, the session host owns work under the agent identity, and `OpenClaw.SessionProtocol` owns their versioned AOT-safe JSON file contract.
- The agent account owns its `PATH` and `NODE_OPTIONS`. Launch requests name a directory or Node.js option and let the session host compose it; never derive either value from the invoking host's environment.
- Reclaim a staged native root only after every launch using it has ended. Each launch holds its root for its lifetime, and a held root remains intact for later setup cleanup; rename or delete success alone is not a lifetime check.
- NativeAOT code uses contract-specific source-generated `JsonSerializerContext` types; do not introduce reflection-based serialization. Run the NativeAOT lane for reflection, interop, startup, CLI identity, trimming, or serialization changes.
- Keep APIs narrow, valid states explicit, nullable analysis intact, and analyzer output clean. Fix diagnostics at the real contract; do not hide them with broad suppressions, casts, widened types, generated baselines, or repository-wide severity changes.
- `clawctl` human output uses the repository's Spectre.Console rendering contract; JSON output is a versioned machine contract. Keep success paths, errors, exit codes, `--no-color`, redirected output, and `FORCE_COLOR` behavior synchronized.
- Diagnostics stay in packaged LocalState or the documented unpackaged fallback, serialize concurrent appends, redact included text, and exclude credential databases and auth profiles. New diagnostics must not become authorization inputs.
- The gateway is scheduled, configured, and observed through its existing owners. Do not force upstream's default port or report the recorded default as an observed listening address.
- MXC archives, Node archives, inventories, metadata, package paths, lengths, and hashes are release trust inputs. Preserve safe unique paths, allowlists, integrity verification, and both architectures.
- PowerShell scripts fail fast and check `$LASTEXITCODE` after native tools. Do not accept command success as a substitute for validating produced metadata, identity, inventory, or hashes.
- Comments explain non-obvious ownership, lifecycle, ordering, platform, integrity, or cleanup constraints, not syntax.

## Product and validation

- Defaults should produce a working, understandable result. Each action has a visible outcome or recorded intentional non-outcome; errors name the next useful step and the diagnostic path when available.
- Preserve the explicit setup, status, gateway-service, collection, PowerShell, and teardown lifecycle. Account deliberately for first run, missing setup, partial setup, concurrent use, unavailable sessions, permission denial, missing dependencies, and recovery.
- Tests protect meaningful observable behavior: return values, protocol results, persisted state, process arguments, exit codes, and rendered output. Do not add source-inspection tests, sleep-based synchronization, ambient-state dependencies, hardcoded ports, or tests that touch real user/package state.
- A regression fix carries a test that would fail on the original defect at the narrowest realistic lane. Do not weaken assertions, increase timeouts, add retries, or skip cases to get green.
- Select proof for the touched contract. Documentation-only changes need `scripts\Test-DocReferences.ps1` and `git diff --check`; ordinary managed-code changes need the quality and test lanes; NativeAOT-sensitive changes need native execution proof; script, packaging, signing, identity, or workflow changes need their owning PowerShell suites.
- Reuse valid proof only when inputs are unchanged. Report every unrun relevant lane and distinguish proven unrelated failures from diff-caused failures.

### Execution gotchas

- Use `scripts\Test-DotNetQuality.ps1` as the canonical restore-plus-Release-analysis gate; do not substitute a build that can skip analyzer execution.
- Ordinary builds and tests leave `IncludePackagingContent` unset. Packaging builds provide runtime, platform, and packaging content and keep their intermediates isolated.
- Restore the session host separately with its target runtime and `PublishAot=true` before a `Build-MSIX.ps1` publish that uses `--no-restore`.
- NativeAOT and MSIX composition need Visual Studio Build Tools and the Windows SDK. Add the Visual Studio Installer directory to `PATH` for `vswhere.exe` as documented; do not diagnose missing NativeAOT components from an ordinary JIT build.
- Use `scripts\Test-NativeAotCli.Tests.ps1` for CLI parsing, help, version, startup, alias identity, trimming, or NativeAOT-sensitive behavior. The JIT xUnit host and a successful publish do not prove the shipped entry point runs.
- Use `scripts\Deploy-LocalPackage.ps1` for the fast loose-registration loop and `scripts\Build-LocalMSIX.ps1` for a real package. Loose registration and installed MSIX state are mutually exclusive and switching can delete packaged app data.

## Authority and safety

- Review and triage are read-only; mutations require task authority. Existing approval carries through the same scoped implementation and recovery, not into releases, official signing, publication, destructive cleanup, or unrelated machine state.
- Never register, remove, replace, or modify a real package, scheduled task, isolated session, user profile, certificate store, or packaged app data from a test. Local deployment against the user's machine requires explicit task scope; `-ReplaceExistingInstall` is destructive because Windows cannot preserve packaged data across loose/MSIX transitions.
- Run the installed-package upgrade harness only on an isolated clean Windows account. Preserve immutable proof-release baselines and their pinned asset names and hashes; clean up only state created by the harness.
- Official signing is allowed only through the reviewed `main` release policy and workflow. Protocol/version bumps, dependency or runtime pin changes, baseline changes, signing, releases, and publication require explicit authorization.
- Keep credentials, private configuration, certificates, auth profiles, and unreleased inputs out of commits, logs, test output, archives, transcripts, and media. Use synthetic fixtures and inspect outgoing artifacts.
- Untrusted contributor or fork code runs only in secretless isolation, never on a trusted signing or release host. Source review alone does not authorize execution with credentials.
- Do not change global Git configuration or `core.hooksPath`, overwrite a hook the repository did not create, bypass hash or metadata checks, weaken tests, or replace a security boundary with documentation.
- Destructive reset, clean, stash, deletion of unrelated work, or modification of a shared checkout requires explicit authorization. Reconcile uncertain writes before retrying.
- Stage only intended files. Keep pull request bodies current with problem, outcome, risk, exact head SHA, validation, and unrun lanes; the selected release workflow owns publication.

## Read when relevant

- **Contributor workflow and commands:** [CONTRIBUTING.md](CONTRIBUTING.md).
- **Runtime and ownership boundaries:** [architecture](docs/architecture.md).
- **Local deployment and validation selection:** [local development](docs/local-development.md).
- **Observable failures and recovery:** [troubleshooting](docs/troubleshooting.md).
- **Console rendering:** [`clawctl` output style](docs/clawctl-output-style.md).
- **MXC behavior and evidence:** [compatibility evidence](docs/mxc-compatibility-evidence.md).
- **Official signing and publication:** [release process](docs/release-process.md), `release-policy.json`, and `.github/workflows/gateway-msix.yml`.
- **Documentation authorities and drift:** [technical documentation skill](.github/skills/technical-documentation/SKILL.md).
- **Scoped implementation rules:** [source](src/AGENTS.md), [scripts](scripts/AGENTS.md), and [tests](tests/AGENTS.md).
