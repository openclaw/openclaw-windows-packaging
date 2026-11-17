# Request Gateway publication to Microsoft Store

The packaging repository builds and retains the authorized Gateway Store bundle.
Store publication runs only in `openclaw/releases`, where a release manager must
approve the exact candidate before the job can request a Microsoft Store token.
The local **Request Microsoft Store publication** workflow is a manual recovery
adapter; it cannot upload or commit a Store submission.

## Activate the centralized publisher

Land and configure the centralized publisher before merging the request-only
cutover. Its workflow is
[`windows-store-publish.yml`](https://github.com/openclaw/releases/blob/main/.github/workflows/windows-store-publish.yml),
and its [publishing guide](https://github.com/openclaw/releases/blob/main/docs/windows-store-publishing.md)
owns approval, identity policy, source authorization, Store receipts, and recovery.
Those links require the coordinated central change to land first.

An administrator must complete and verify these separate changes:

1. In `openclaw/releases`, configure the `microsoft-store-gateway` environment
   for the exact `main` branch, require
   `@openclaw/release-managers-openclaw` approval, and disable administrator
   deployment bypass. Keep the central source-validation job credential-free;
   only the approved publisher job may request Store OIDC tokens.
2. Grant that team explicit repository write access and enforce code-owner review
   on `main` in both repositories, including new commits after approval and the
   workflow, publisher dependencies, identity policies, and CODEOWNERS itself.
   This repository's CODEOWNERS file declares the owners; it does not enable or
   enforce a GitHub branch rule.
3. Install the existing release GitHub App (ID `2729701`) for `openclaw/releases`
   with Actions write access. Make its existing `GH_APP_PRIVATE_KEY` available
   to this repository's manual request workflow. The minted token is scoped to
   the `releases` repository and Actions write; it does not grant Store access.
4. Move the dedicated Gateway Partner Center application's federation to the
   central Gateway environment. Verify the exact subject against the central
   repository's current OIDC subject configuration. Remove the old packaging
   `microsoft-store` federated credential and any other publishing writer; an
   old trusted workflow revision otherwise remains a bypass. Keep the dedicated
   Store application separate from the unchanged Azure sideload-signing identity.
5. Move the Gateway flight GUID into the central repository variable
   `MSSTORE_GATEWAY_DEVELOPMENT_FLIGHT_ID`. After manual qualification, use the
   central `MSSTORE_GATEWAY_DEVELOPMENT_ENABLED` activation flag. Remove obsolete
   native Store environment variables and both `MSSTORE_ENABLED` and
   `MSSTORE_DEVELOPMENT_ENABLED` toggles after the cutover. Do not start a legacy publisher while
   changing its cloud trust, and reconcile any in-flight submission first.

No new PAT or persistent Store client secret is required. GitHub App availability,
team permissions, environment review rules, branch rules, and exclusive Entra
federation are administrative prerequisites, not guarantees made by this YAML.
The centralized implementation and these settings are a merge/cutover dependency;
this document does not claim they have already been activated.

## Request publication manually

Choose the successful run ID for a canonical main-branch **Build OpenClaw Gateway
MSIX** dispatch in `store` or `official` mode. It must have completed successfully
and retained the unexpired `openclaw-gateway-msix-store-submission-<attempt>`
bundle and `store-release-source-<attempt>` witness. The
central publisher independently authorizes the run and validates the exact bundle;
the request adapter does not make an eligibility decision.

From **Actions > Request Microsoft Store publication > Run workflow**, select
`main`, enter `run_id`, and select `production` for stable or `development` for
a published beta. The equivalent Development-flight command is:

```powershell
gh workflow run submit-microsoft-store.yml `
  --repo openclaw/openclaw-windows-packaging --ref main `
  -f run_id=RELEASE_RUN_ID -f channel=development
```

This sends `{product: gateway, run_id: RELEASE_RUN_ID, channel, automatic: false}` to the standalone central
workflow on `main`. Follow the central run for its source commit, version,
run/attempt, and bundle hash, then release-manager approval and Store receipt.
A successful request means GitHub accepted the request, not that Microsoft
accepted a bundle or completed certification. No package is rebuilt, no GitHub
release is republished, and no Store token is minted here.

## Automatic requests and recovery

This repository has no `workflow_run` Store trigger and no automatic-publication
variable. Gateway's upstream release tooling can request the centralized publisher
after building the exact released source; it must not also dispatch this manual
recovery adapter for the same candidate. Manual recovery and automatic callers
share the central publisher's approval and replay policy, not a second local
publisher.

## Development flight

The coordinated core release handoff requests `channel=production` after stable
builds and `channel=development` after beta builds, with `automatic=true`.
Beta builds use `official` mode and publish their signed GitHub prerelease before
retaining the separate unsigned Store bundle and attempt-bound witness.
The central publisher checks that published prerelease and keeps Development
delivery in its configured flight, never in the public production submission.
Both destinations require release-manager approval. A Store flight is an opt-in
update path for enrolled Microsoft Store accounts, not a separate public `/beta`
listing; sideload GitHub packages retain their separate identity.

Before activating automatic Development delivery, finish or explicitly resolve
the initial flight draft, qualify a manual submission, and verify certification
plus a tester's Store install/upgrade and retained data. Leave the central
activation flag unset until then. Its
[administrator guide](https://github.com/openclaw/releases/blob/main/docs/windows-store-publishing.md)
owns the flight GUID, activation, approval policy, and API execution. No new Store
credential or per-flight federation is introduced by this adapter.

## Verify and recover

Before retrying an uncertain request, inspect the central runs and Partner Center
state. A central receipt containing `CommitStarted` proves commit acceptance,
not certification or public availability. Preserve the central workflow URL and
submission evidence, and follow certification in Partner Center. Do not blindly
retry an accepted or ambiguous submission, overwrite an existing draft, or edit
an API-created draft while the publisher runs. If the source artifact expired,
produce and validate a new candidate using the
[release process](release-process.md).

The unsigned multi-architecture Store bundle and its build/upgrade evidence remain
owned by this repository. Signed sideload release assets are not Store inputs.
The packaging repository no longer contains a Store API engine, Store submission
policy, or Store source authorizer; centrally reviewed code owns those decisions.

## Store package versions

Store bundles keep the builder's shared stable/beta sequence:
`year.month.(10000 + github.run_number).0`. The year and month come from the
captured release identity, not the payload compatibility pin or wall clock.
Run numbers must stay within `1..55534`; retries reuse the same allocated version.
Do not reset or rename the release workflow to reset its counter. The offset
keeps these packages above the earlier source-derived range; leaving a newer
flight does not downgrade an installation to an older production package.

[`Get-WorkflowPackageVersion.ps1`](../scripts/Get-WorkflowPackageVersion.ps1)
owns allocation and bounds. Store and sideload composition remain independent
so compiled CLI versions match their manifests. The central publisher neither
allocates another version nor rebuilds these bundles.
