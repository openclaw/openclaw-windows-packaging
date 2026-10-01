# Gateway MSIX release process

Use this guide to prepare and publish an OpenClaw Gateway MSIX release. It is a
maintainer how-to: the release uses the reviewed stable-channel policy and ends
when the GitHub Release and its transition evidence are available. For signing
prerequisites, MSIX identity rules, and the identity
examples, see the [official signing setup](../README.md#official-signing-setup)
in the README.

## Release decision and policy change

`release-policy.json` controls source selection and package identity. The default
follows the latest Gateway stable release; a new upstream release does not need
a policy update. Change policy in a reviewed pull request when needed:

| Field | Owner and purpose |
|---|---|
| `repository` | The upstream OpenClaw repository that the package source must come from. |
| `channel` | `stable`, resolved once per workflow run from public npm `openclaw@latest`. |
| `stableVersion` | Optional exact stable version for an exceptional reviewed compatibility pin; omit to follow latest. |
| `msixRevision` | The packaging rebuild number used to derive the MSIX and GitHub release identity. |
| `packageIdentityName` | The Partner Center-reserved MSIX identity name. |
| `packageFamilyName` | The expected Windows package family name for that identity and publisher. |
| `publisher` | The expected MSIX publisher subject used by packaging and signing validation. |
| `sideloadPackageIdentity` | The Azure-signable legacy identity used for signed GitHub Release packages and transition proof. |

Keep `repository`, package identity fields, and `publisher` aligned with the
reviewed release trust boundary. Set `msixRevision` to `0` for a new Gateway
tag's first package; reset it in a reviewed change if a previous rebuild
increased it. Increment it only for a packaging rebuild of the same Gateway
tag. Do not assign a release tag or MSIX version by hand:
`scripts\Get-MSIXReleaseIdentity.ps1` derives them from the run's resolved
Gateway tag and `msixRevision`.

Leave the workflow's `openclaw_ref` default empty: packaging runs follow the
stable source selection described in the [README](../README.md#selecting-the-openclaw-revision).
Set the optional `gateway_version` input to an exact stable version for a one-run
payload rollback. The payload uses that older version, while package and release
identity continue from the unmodified policy pin or npm latest so the MSIX remains
an upgrade. Both verified sources are captured and replayed with the immutable
snapshot; the input does not change release policy and must be older than the
release-identity source.
For `store` and `official`, leave `openclaw_ref` empty or supply the full commit
SHA of the selected payload release. The SHA is a match assertion, not a source override;
tags, branches, and nonmatching commits are rejected. The workflow also
accepts `signing_mode`, whose choices are `unsigned`, `test`, `store`, and
`official`. Select `store` to authorize and retain an unsigned Partner Center
submission bundle without signing. Select `official` to retain that Store
bundle and sign the separate sideload-identity packages for GitHub Releases.
Following stable does not automatically publish: both modes still require a
manual dispatch from `main`.

## Before dispatch

Complete this checklist before dispatch. Merge any needed policy changes to
`main` first.

1. Confirm the dispatch target is `main` and select `signing_mode=store` for a
   Partner Center submission or `official` for compatible Azure signing.
   Leave `gateway_version` empty to package policy/latest, or set the exact older
   stable payload required for this run. Confirm the resulting MSIX identity is
   newer than the installed production baseline.
   Leave `openclaw_ref` empty to select stable, or supply the matching full SHA
   to assert the intended source. Release publication is rejected for every
   other workflow branch.
   Keep `publish_release=true` for a new official release. Set it to `false`
   only when producing signed artifacts for an explicitly reviewed recovery.
2. Confirm the policy selects the intended stable channel and publisher. Check
   for an exceptional `stableVersion` pin and remove it in a reviewed change
   if the release identity should follow latest. The run records the exact
   upstream commit, tag, and version for both release identity and payload when
   `gateway_version` differs.
3. Confirm the derived identity with
   `scripts\Get-MSIXReleaseIdentity.ps1` rather than calculating a version or
   release tag manually. Use the README's [identity guidance](../README.md#official-signing-setup)
   for the version scheme.
4. Review the release-facing pull request titles. The published release notes
   are generated from merged pull request titles; `CONTRIBUTING.md` owns the
   required title format.
5. Confirm every relevant versioning or source-selection pull request's
   `test-msix-upgrades` matrix job succeeded
   and retained its identity-transition or in-place-upgrade evidence. The
   matrix runs only on pull requests that change versioning inputs or
   source-selection scripts; it does not run during the later official
   dispatch.
6. Do not reuse or alter an accepted GitHub release tag. Update the latest
   production entries in `scripts\msix-upgrade-baselines.json` only in a
   reviewed policy pull request, using the verified digest of an immutable
   signed sideload release.

Dispatch **Build OpenClaw Gateway MSIX** from `main` in GitHub Actions with
those inputs. A `store` or `official` run deliberately bypasses both the upstream package
cache and the architecture-specific Windows dependency-tree cache, so its
payload is rebuilt and revalidated.

## Watch the release workflow

The workflow first captures one immutable source snapshot in `source-preflight`.
It selects exactly one caller: `build-trusted` for main/stable with cache-write
access, or `build-read-only` for overrides, non-main, and pull requests.
Both call `.github\workflows\gateway-msix-build.yml`, which inherits the
caller's service-enforced cache cap without requesting a stronger mode.
No signing secrets or OIDC capability enter upstream execution.
The `build` job normalizes outputs and rejects missing, failed, cancelled,
double, or wrong-route builds, and source identity mismatches.
Documentation-only pull requests intentionally skip both callers.

The shared workflow builds the validated upstream package, then builds the
expanded payload and unsigned MSIX separately for both x64 and ARM64. It
creates Store-identity and sideload-identity packages for each architecture and
composes a multi-architecture bundle for each channel with
`scripts\Build-MSIXBundle.ps1`. Both channels use the same validated payload
and derived package version.

Before publication or Azure credentials, `authorize-signing` runs
`scripts\Test-SigningInputs.ps1`. It restores the same run's source snapshot
and validates its workflow run ID, packaging commit, signing mode, workflow
ref, Git ref, and event without
resolving latest again. That recorded source supplies the expected upstream
commit, payload version, and derived release identity. The check validates
policy-controlled publishers and every Store and sideload package and bundle
against those inputs. A later channel change or upstream tag deletion does not
rewrite or revoke a captured source snapshot; stop the workflow rather than
retrying it if that captured release must no longer be published. For `official`,
only after both channels
pass does `sign-msix` use Azure login. It signs only the sideload x64, ARM64,
and bundle artifacts; the Store bundle remains unsigned for Partner Center.

Observe these workflow outcomes:

- Exactly one build caller and the normalized `build` job succeed.
- `build-msix` succeeds for **both** x64 and ARM64.
- `test-host` and `build-msix-bundle` succeed before `authorize-signing`.
- `authorize-signing` succeeds before Azure login and signing begins.
- `sign-msix` verifies signatures and refreshes package metadata.

The pull request's upgrade matrix uses
`scripts\Test-MSIXUpgrade.ps1` with the immutable, hash-pinned release fixtures in
`scripts\msix-upgrade-baselines.json`. It requires an isolated clean Windows
account per scenario and refuses to run when an OpenClaw Gateway package is
already registered. It exercises two explicit modes for every baseline and
both standalone and bundle delivery in parallel:

- **Store identity reset:** install a signed sideload baseline, write a
  LocalState marker, remove that identity, install the test-signed Store
  candidate, and prove the reserved package family has isolated LocalState.
  This remains the Store gate until Partner Center has distributed an authentic
  Store-signed baseline.
- **Sideload in-place update:** install the same baseline, write the marker,
  update directly to the newer test-signed sideload candidate without removing
  the package, and prove the package family and exact marker are retained.
- **Store in-place update:** install the published Store product by its Store
  ID, record the installed version after validating the reserved identity,
  require the candidate to be newer, write the marker, and update through the
  test-signed Store bundle. The package family and exact marker must survive.

Before approving a release, the release owner must confirm that the
candidate version is newer than the version Microsoft Store currently installs.
A missing or differently identified Store package is a hard failure. If no
installed Store baseline exists, record that owner decision and retain the
reset/fresh-install evidence. Do not substitute a developer profile or weaken
the fixtures to make either check pass.

## Publication and completion

After authorization succeeds, `retain-store-submission` keeps the unsigned
Store-identity bundle as the `openclaw-gateway-msix-store-submission` Actions
artifact for 90 days. Microsoft signs it during Store ingestion; it is never a
GitHub Release download. `publish-release` runs after sideload signing and creates the permanent GitHub release
tag derived by `scripts\Get-MSIXReleaseIdentity.ps1`, generates release notes
from merged pull request titles, and publishes the signed multi-architecture
bundle plus signed x64 and ARM64 standalone MSIX assets.

After publication, verify the release has the derived permanent tag, generated
notes, three signed sideload assets, and valid signatures. Separately verify
the run retained its unsigned Store submission bundle. Reconcile the release with the successful
identity-transition and in-place-upgrade evidence from the relevant pull request.
That evidence applies to the source it tested; when stable has advanced, record
the validation gap and obtain current-source upgrade proof before release. Verify the bundle and both
standalone packages are present; the bundle is the multi-architecture delivery,
while the standalone packages support explicit architecture deployment. Keep
both the release workflow run and the relevant pull request's evidence available
as the release record.

## Failure and rollback boundaries

Never mutate an accepted release tag or a proof-release baseline to repair a
failed release. If the upstream tag and accepted commit are unchanged and only
packaging must be rebuilt, increment `msixRevision` in a reviewed policy
change, then repeat the process with the exact same upstream tag and commit.
If stable has moved, set `gateway_version` to the exact older stable payload for
the rebuild; release identity still follows the unmodified stable selector. A
longer-lived compatibility pin can remain in reviewed policy as `stableVersion`;
remove it and reset `msixRevision` to `0` before publishing a new Gateway tag. A
signing-authorization or upgrade-validation
failure is a stop condition: correct the reviewed inputs or packaging defect,
then dispatch a new compliant run rather than publishing partial artifacts.

## Cache-boundary cutover

The configured cache token boundary needs platform proof after publication;
local owner tests do not prove scoped-token enforcement or fresh CodeQL
results. Official/store bypass the explicit package and payload caches, but
setup-dotnet and nested upstream actions may still use caches under the caller
cap. Unsigned/test arbitrary-source dispatches remain read-only even on main.
The `cache_write` input merely avoids explicit save attempts; it is not the
authorization boundary and cannot grant writes to a read-capped caller.

Before trusting main caches under the hardened workflow, coordinate an
authorized activation window: let legacy writers finish, prevent racing
legacy restores/writes, re-enumerate main-scoped cache IDs, and invalidate only
the approved IDs (including CodeQL caches). Do not release against legacy
cache contents during this transition. A cold build costs a full rebuild;
then verify a warm trusted build reuses caches and an arbitrary-source run
cannot save, including attempts from upstream subprocesses with altered
environment. Verify GitHub accepts the workflow keys and propagates the
caller cap to reusable jobs, including implicit NuGet and upstream caches.
If it rejects the configuration or enforcement cannot be shown, stop; an
environment flag or a different cache key is not a substitute boundary.

Run fresh changed-revision CodeQL analysis before deciding alert outcomes.
Cache alerts remain open unless the fresh scan closes them or separately
authorized disposition is backed by actual platform denial evidence.
Cache deletion, dispatch, alert disposition, signing, and publication are
separate authorized operations, not part of local implementation validation.
