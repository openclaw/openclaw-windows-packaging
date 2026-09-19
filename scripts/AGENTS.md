# Script implementation guide

Read the repository-root `AGENTS.md` before this file. This scope owns build, payload, packaging, signing, deployment, release-validation, and policy-test scripts.

## Script contract

- Set `$ErrorActionPreference = 'Stop'` and check `$LASTEXITCODE` after every native command whose failure matters. Preserve the original failure when cleanup also fails.
- Resolve repository-root paths explicitly and keep documented commands runnable from the root in PowerShell 7.
- Keep x64 and ARM64 behavior synchronized. Architecture-specific files, runtime identifiers, platform properties, payload metadata, package identity, inventory, and signing validation move together.
- Treat `mxc-runtime.lock.json`, Node archive input, payload metadata, source metadata, application inventories, upgrade baselines, release policy, and emitted hashes as trust inputs. Validate content, not just command exit.
- Reject unsafe or duplicate archive paths, unexpected files, length/hash mismatches, identity drift, and stale metadata. Never loosen an allowlist merely to accept new output.
- Ordinary builds leave `IncludePackagingContent` unset. Packaging restores and publishes with the target runtime and platform; restore the session host separately before its `--no-restore` publish.
- `LocalPackage.psm1` owns the loose-layout workflow. Keep GitHub access, publish, registration, package removal, and deployment operations injectable so scenario tests cannot mutate real state.
- Loose registration and MSIX installation are mutually exclusive. Preserve the explicit `-ReplaceExistingInstall` acknowledgement and never imply packaged app data survives the transition.
- Preserve immutable upgrade baselines and clean up only registrations and files created by the current harness run. Run the harness only in its documented isolated account.
- Official signing bypasses reusable build caches and remains gated by `release-policy.json`, `main`, immutable upstream identity, and signing-input validation before credentials are requested.
- A new or changed script behavior needs the corresponding `Test-*.Tests.ps1` scenario at the owning boundary. Use fixture directories and injected operations; do not register packages, install certificates, invoke signing services, or alter scheduled tasks.

## Proof

- Run the narrowest owning PowerShell suite named in `CONTRIBUTING.md` and `docs/local-development.md`; run `scripts\Test-PackagingRelevance.Tests.ps1` when path classification changes.
- Run `scripts\Test-SigningInputs.Tests.ps1` for release-policy, workflow-signing, inventory, or official-input changes.
- Run `scripts\Test-DocReferences.ps1` plus its test suite for documentation-checker changes. Report packaging, signing, deployment, or upgrade lanes not run.
