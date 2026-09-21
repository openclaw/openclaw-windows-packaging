# Official release process

Use this guide to prepare and publish an official OpenClaw Gateway MSIX
release. It is a maintainer how-to: the release starts with a reviewed policy
change and ends when the signed GitHub Release and its upgrade evidence are
available. For signing prerequisites, MSIX identity rules, and the identity
examples, see the [official signing setup](../README.md#official-signing-setup)
in the README.

## Release decision and policy change

`release-policy.json` is the release decision record. Update it in a reviewed
pull request when accepting a new upstream release:

| Field | Owner and purpose |
|---|---|
| `repository` | The upstream OpenClaw repository that the package source must come from. |
| `gatewayTag` | The accepted stable upstream Gateway tag; it contributes to the release identity. |
| `msixRevision` | The packaging rebuild number used to derive the MSIX and GitHub release identity. |
| `payloadPackageVersion` | The expected version in the validated upstream payload. |
| `approvedCommit` | The immutable upstream commit approved for official signing. |
| `publisher` | The expected MSIX publisher subject used by packaging and signing validation. |

For a new upstream tag, change `gatewayTag`, `approvedCommit`, and
`payloadPackageVersion` together after verifying that the tag resolves to that
commit and that its payload reports that version. Keep `repository` and
`publisher` aligned with the reviewed release trust boundary. Set
`msixRevision` to `0` for the tag's first package. Do not assign a release tag
or MSIX version by hand: `scripts\Get-MSIXReleaseIdentity.ps1` derives them
from `gatewayTag` and `msixRevision`.

Leave the workflow's `openclaw_ref` default empty: packaging runs follow the
stable source selection described in the [README](../README.md#selecting-the-openclaw-revision).
That selection does not grant official-signing approval. For an official
dispatch, supply the full `approvedCommit`, or leave the input empty only when
the selected stable release matches the reviewed policy. The workflow also
accepts `signing_mode`, whose choices are `unsigned`, `test`, and `official`;
select `official` only for the approved release dispatch.

## Before dispatch

Complete this checklist after the policy pull request has merged to `main`.

1. Confirm the dispatch target is `main` and `signing_mode` is `official`.
   Set `openclaw_ref` to the policy's full `approvedCommit`, or leave it empty
   to select stable. Official signing is rejected for every other branch.
2. Confirm the accepted immutable commit, payload version, and publisher match
   `release-policy.json`. If leaving `openclaw_ref` empty, confirm the selected
   stable source matches that same approved commit and version.
3. Confirm the derived identity with
   `scripts\Get-MSIXReleaseIdentity.ps1` rather than calculating a version or
   release tag manually. Use the README's [identity guidance](../README.md#official-signing-setup)
   for the version scheme.
4. Review the release-facing pull request titles. The published release notes
   are generated from merged pull request titles; `CONTRIBUTING.md` owns the
   required title format.
5. Confirm the policy pull request's `test-msix-upgrades` job succeeded and
   retained its upgrade-evidence artifact. That job runs only on pull requests
   that change versioning inputs or source-selection scripts; it does not run
   during the later official dispatch.
6. Do not reuse or alter an accepted GitHub release tag, and do not edit an
   existing proof-release entry in `scripts\msix-upgrade-baselines.json`.

Dispatch **Build OpenClaw Gateway MSIX** from `main` in GitHub Actions with
those inputs. An official run deliberately bypasses both the upstream package
cache and the architecture-specific Windows dependency-tree cache, so its
payload is rebuilt and revalidated.

## Watch the release workflow

The workflow first builds the validated upstream package, then builds the
expanded payload and unsigned MSIX separately for both x64 and ARM64. It
creates standalone packages for each architecture and composes one
multi-architecture bundle with `scripts\Build-MSIXBundle.ps1` before official
signing. `scripts\Build-MSIX.ps1` supplies the architecture-specific packages;
the bundle script requires distinct x64 and ARM64 package inputs and the
derived package version.

Before Azure credentials are requested, `authorize-signing` runs
`scripts\Test-SigningInputs.ps1`. That check validates the immutable requested
commit, policy-controlled payload and publisher inputs, package and bundle
identity, and the release artifacts. Only after it succeeds does `sign-msix`
use Azure login. The workflow signs the x64 and ARM64 standalone packages and
the already-composed bundle; signing the bundle covers its contained packages.

Observe these workflow outcomes:

- `build-msix` succeeds for **both** x64 and ARM64.
- `build-msix-bundle` succeeds before `authorize-signing`.
- `authorize-signing` succeeds before Azure login and signing begins.
- `sign-msix` verifies signatures and refreshes package metadata.

The policy pull request's upgrade job uses `scripts\Test-MSIXUpgrade.ps1` with
the immutable, hash-pinned proof-release fixtures in
`scripts\msix-upgrade-baselines.json`. It requires an isolated clean Windows
account and refuses to run when an OpenClaw Gateway package is already
registered. It installs every standalone and bundle baseline, upgrades each to
the candidate, proves LocalState is retained, and proves both candidate
delivery formats install cleanly. Do not substitute a developer profile or
weaken those fixtures to make the check pass.

## Publication and completion

After signing succeeds, `publish-release` creates the permanent GitHub release
tag derived by `scripts\Get-MSIXReleaseIdentity.ps1`, generates release notes
from merged pull request titles, and publishes the signed multi-architecture
bundle plus signed x64 and ARM64 standalone MSIX assets.

After publication, verify the release has the derived permanent tag, generated
notes, and all three signed assets. Reconcile the release with the successful
upgrade evidence from the policy pull request. Verify the bundle and both
standalone packages are present; the bundle is the multi-architecture delivery,
while the standalone packages support explicit architecture deployment. Keep
both the release workflow run and the policy pull request's evidence available
as the release record.

## Failure and rollback boundaries

Never mutate an accepted release tag or a proof-release baseline to repair a
failed release. If the upstream tag and accepted commit are unchanged and only
packaging must be rebuilt, increment `msixRevision` in a reviewed policy
change, then repeat the process with the exact same upstream tag and commit.
For a new upstream tag, update the reviewed policy inputs together--tag,
immutable commit and payload version--and
start a new release decision. A signing-authorization or upgrade-validation
failure is a stop condition: correct the reviewed inputs or packaging defect,
then dispatch a new compliant run rather than publishing partial artifacts.
