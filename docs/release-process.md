# Gateway MSIX release process

Use this guide to prepare and publish an OpenClaw Gateway MSIX release. It is a
maintainer how-to: the release starts with a reviewed policy change and ends
when the GitHub Release and its transition evidence are available. For signing
prerequisites, MSIX identity rules, and the identity
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
| `packageIdentityName` | The Partner Center-reserved MSIX identity name. |
| `packageFamilyName` | The expected Windows package family name for that identity and publisher. |
| `publisher` | The expected MSIX publisher subject used by packaging and signing validation. |
| `sideloadPackageIdentity` | The Azure-signable legacy identity used for signed GitHub Release packages and transition proof. |

For a new upstream tag, change `gatewayTag`, `approvedCommit`, and
`payloadPackageVersion` together after verifying that the tag resolves to that
commit and that its payload reports that version. Keep `repository` and
package identity fields and `publisher` aligned with the reviewed release
trust boundary. Set
`msixRevision` to `0` for the tag's first package. Do not assign a release tag
or MSIX version by hand: `scripts\Get-MSIXReleaseIdentity.ps1` derives them
from `gatewayTag` and `msixRevision`.

Leave the workflow's `openclaw_ref` default empty: packaging runs follow the
stable source selection described in the [README](../README.md#selecting-the-openclaw-revision).
That selection does not grant official-signing approval. For an official
dispatch, supply the full `approvedCommit`, or leave the input empty only when
the selected stable release matches the reviewed policy. The workflow also
accepts `signing_mode`, whose choices are `unsigned`, `test`, `store`, and
`official`. Select `store` to authorize and retain an unsigned Partner Center
submission bundle without signing. Select `official` to retain that Store
bundle and sign the separate sideload-identity packages for GitHub Releases.

## Before dispatch

Complete this checklist after the policy pull request has merged to `main`.

1. Confirm the dispatch target is `main` and select `signing_mode=store` for a
   Partner Center submission or `official` for compatible Azure signing.
   Set `openclaw_ref` to the policy's full `approvedCommit`, or leave it empty
   to select stable. Release publication is rejected for every other branch.
   Keep `publish_release=true` for a new official release. Set it to `false`
   only when producing signed artifacts for an explicitly reviewed recovery.
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
5. Confirm every policy pull request `test-msix-upgrades` matrix job succeeded
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

The workflow first builds the validated upstream package, then builds the
expanded payload and unsigned MSIX separately for both x64 and ARM64. It
creates Store-identity and sideload-identity packages for each architecture and
composes a multi-architecture bundle for each channel with
`scripts\Build-MSIXBundle.ps1`. Both channels use the same validated payload
and derived package version.

Before publication or Azure credentials, `authorize-signing` runs
`scripts\Test-SigningInputs.ps1`. That check validates the immutable requested
commit, policy-controlled payload and publisher inputs, and every Store and
sideload package and bundle identity. For `official`, only after both channels
pass does `sign-msix` use Azure login. It signs only the sideload x64, ARM64,
and bundle artifacts; the Store bundle remains unsigned for Partner Center.

Observe these workflow outcomes:

- `build-msix` succeeds for **both** x64 and ARM64.
- `test-host` and `build-msix-bundle` succeed before `authorize-signing`.
- `authorize-signing` succeeds before Azure login and signing begins.
- `sign-msix` verifies signatures and refreshes package metadata.

The policy pull request's upgrade matrix uses
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
  ID, require the installed package to match the reviewed previous version and
  reserved identity, write the marker, and update directly to each test-signed
  Store candidate format. The package family and exact marker must survive.

Before approving a policy bump, the release owner must confirm whether the
previous Store revision reached users. When it did, the Store install must
resolve to that exact version before the in-place test continues; a newer,
older, missing, or differently identified Store package is a hard failure. If
no installed Store baseline exists, record that owner decision and retain the
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
identity-transition and in-place-upgrade evidence from the policy pull request. Verify the bundle and both
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
