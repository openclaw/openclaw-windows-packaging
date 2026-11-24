# Request Gateway publication to Microsoft Store

The packaging repository builds and retains the authorized Gateway Store bundle.
Store publication runs only in `openclaw/releases`, where a release manager must
approve the exact candidate before the job can request a Microsoft Store token.
The local **Request Microsoft Store publication** workflow requests approval after
an opted-in release completes, and supports manual recovery. It cannot upload or
commit a Store submission.

## Activate the centralized publisher

The centralized publisher's workflow is
[`windows-store-publish.yml`](https://github.com/openclaw/releases/blob/main/.github/workflows/windows-store-publish.yml),
and its [publishing guide](https://github.com/openclaw/releases/blob/main/docs/windows-store-publishing.md)
owns approval, identity policy, source authorization, Store receipts, and recovery.
Verify its [administrator cutover](https://github.com/openclaw/releases/blob/main/docs/windows-store-publishing.md#administrator-cutover)
before using the request adapter. Merged code does not establish that live
settings or publication have been qualified.

An administrator must complete and verify these separate changes:

1. In `openclaw/releases`, configure the `microsoft-store-gateway` environment
   for the exact `main` branch, require
   `@openclaw/release-managers-openclaw` approval, and disable administrator
   deployment bypass. Keep the central source-validation job credential-free;
   only the approved publisher job may request Store OIDC tokens.
2. Verify that team has access to review deployments in `openclaw/releases`.
   Leave existing branch rules, CODEOWNERS, and required-CI settings unchanged.
   This cutover does not require PR-only `main` or release-manager code review;
   repository writers remain trusted to change publisher code. Environment
   approval gates the credential-bearing job, not changes to its implementation.
3. Install the existing release GitHub App (ID `2729701`) for `openclaw/releases`
   with Actions write access. Make its existing `GH_APP_PRIVATE_KEY` available
   to this repository's request workflow. The minted token is scoped to
   the `releases` repository and Actions write; it does not grant Store access.
   That token restriction does not constrain the private key itself: audit the
   App's installation grants and key availability separately.
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
team permissions, environment review rules, and exclusive Entra
federation are administrative prerequisites, not guarantees made by this YAML.
Verify these settings independently of the implementation. Migrating central
release-evidence workflows to PRs is not a Store-publishing prerequisite.

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

The upstream release request dispatches an `official` build with
`publish_release=true` and `request_store_publication=true`. The latter defaults
to false for manual builds. Only that combination records an automatic opt-in in
the attempt-bound Store witness. Stable builds publish signed GitHub releases;
beta builds publish signed GitHub prereleases.
Opted-in builds require the captured payload and release identity to match before
building or signing. If stable advanced before source capture, the old request
fails instead of publishing an old payload under the newer release's identity.
Opting in without official signing and GitHub publication fails before source
resolution; it never silently turns the request off.

The adapter uses `workflow_run: completed`, not polling or a step inside the
still-running build. Before acquiring the release App token, it requires a
successful canonical main-branch manual build and reads that attempt's unique,
unexpired witness. The current API run must still match the completed event's
successful attempt and commit. The witness must match the run, attempt, and packaging commit
and explicitly opt in. Legacy witnesses and ordinary builds do not dispatch.
Stable requests select production; beta requests select development. The central
publisher independently authorizes source, artifacts, and bundle bytes.
The adapter selects a run, not an approved candidate. The central publisher
captures its current attempt and exact bundle for approval, then revalidates that
same candidate before Store access. Reruns retain the workflow's original inputs
and source snapshot; a superseded or running candidate cannot reuse approval.

Land this packaging support before updating core to send the new input. Remove
core's old build polling and central Store dispatch in that same caller update:
build completion is the only automatic request owner. Core's successful dispatch
does not mean the build or Store publication has finished. Follow the packaging
run, its completion adapter, and then the central approval run.

Do not rerun an automatic request workflow after an uncertain dispatch; it
rejects second attempts. Inspect existing central requests and receipts first,
then use the manual adapter only when recovery is needed. Manual recovery and
automatic callers share the central publisher's approval and replay policy,
not a second local publisher.

## Development flight

The completed-release adapter requests `channel=production` after stable
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
