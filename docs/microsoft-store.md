# Submit a release to Microsoft Store

Use this guide to submit an already validated release bundle, retry a failed
Store delivery without rebuilding, or enable automatic Store delivery. This
workflow does not change signing, release approval, or install/upgrade acceptance.
GitHub releases and the existing unsigned Store artifact handoff remain available.

## Configure the publishing identity

1. Link the correct Microsoft Entra tenant to Partner Center. Add a dedicated
   Entra application to Partner Center with the **Manager (Windows)** role.
   The Store product must already have a completed, published first submission
   and completed age ratings. Do not reuse the Azure code-signing identity.
2. Add one federated credential to that Entra application:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Audience: `api://AzureADTokenExchange`
   - Subject: `repo:openclaw@252820863/openclaw-windows-packaging@1347889239:environment:microsoft-store`
   Verify the repository's current OIDC subject configuration before provisioning;
   immutable-subject settings affect the exact subject. The dedicated application
   must have no client secrets, certificates, or other federated writers.
3. In the repository's **Settings > Environments > microsoft-store**, set these
   environment variables (not secrets):
   - `MSSTORE_TENANT_ID`: Entra tenant GUID.
   - `MSSTORE_CLIENT_ID`: dedicated application's client GUID.
   - `MSSTORE_APPLICATION_ID`: Partner Center Store product ID, not the client GUID.
4. Allow deployments from the `main` **branch** in this environment. The submission
   workflow always runs trusted main code, including when it consumes a tag's
   bundle. Restrict the environment to main, and configure release-owner approval
   if required by your publishing policy. Tag-only protection will block this job.
5. Leave the repository variable `MSSTORE_ENABLED` unset until the first manual
   submission and the required Store install/upgrade acceptance have succeeded.
   No persistent Store password is required.

## Submit manually

Merge the workflow onto main first. Select the successful run ID for a main-branch **Build OpenClaw Gateway MSIX** dispatch in `store` or `official` mode.
The ID is the number after `/actions/runs/` in its URL. The source run must have
completed successfully and retained its unexpired authorized Store bundle.

From **Actions > Submit Microsoft Store > Run workflow**, select `main` and
enter `run_id`. The equivalent CLI command is:

```powershell
gh workflow run submit-microsoft-store.yml `
  --repo openclaw/openclaw-windows-packaging --ref main `
  -f run_id=RELEASE_RUN_ID
```

Manual dispatch deliberately does not require `MSSTORE_ENABLED`. It uses the same
source checks, protected environment, OIDC identity, and publisher as automatic
submission. It never rebuilds a package or republishes the GitHub release.

Only the multi-architecture Store `.msixbundle` is uploaded. The publisher checks
its identity against repository policy and the actual Partner Center product
before creating a draft. It preserves listing, pricing, availability, and other
product metadata while replacing the packages with the selected bundle.

## Enable automatic delivery

After the manual rehearsal and release acceptance, set the **repository** Actions
variable `MSSTORE_ENABLED` to `true`. It must be repository-scoped because the
source job checks it before entering the protected environment. Successful eligible
release runs then trigger **Submit Microsoft Store** through `workflow_run`.
PRs, ordinary main builds, failed runs, and unrelated prereleases do not submit. Ineligible
automatic runs are skipped; selecting an ineligible run manually fails explicitly.

Remove the variable or set it to `false` to pause future automatic submissions.
This does not cancel a submission already in progress. Both manual and automatic
runs share one concurrency group and never cancel an active Store upload.

## Verify and recover

A successful submission means Partner Center returned `CommitStarted`, not that
certification or public rollout has finished. Retain the Store workflow URL and
its 90-day submission evidence artifact. The evidence includes the bundle SHA-256,
product ID, exact submission ID, metadata hashes, and commit acceptance. Follow
certification and publication in Partner Center before declaring Store delivery
complete. The current policy requests a 100% package rollout and preserves the
product's publication mode, including manual or scheduled publication.

Before dispatch, finish or explicitly discard any existing draft in Partner
Center. Automation rejects existing drafts and never replaces them. Do not edit
an API-created draft in the portal while automation runs. A failure before commit
can remove only the draft created by this run. A lost or ambiguous commit response
preserves that draft: inspect Partner Center before retrying. Never blindly rerun
a submission that may already have been accepted. If the release artifact expired,
use the release process to produce and validate a new candidate.

See Microsoft's [submission API](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions)
and [application identity resource](https://learn.microsoft.com/en-us/windows/uwp/monetize/get-app-data).

## Development flight

The same protected publisher sends published beta Gateway MSIX GitHub prereleases
to a **Development** package flight, never to the public production submission.
Stable releases still use the production destination and its separate activation flag.

1. In Partner Center, create a known-user group named **OpenClaw Development
   Testers**. Add each tester's Microsoft account email address, which must be the
   account they use in Microsoft Store.
2. From the product overview, create a **Development** package flight and select
   that group. Flights belong to a product: Companion and Gateway require their
   own flight even when they share the tester group.
3. Set `MSSTORE_DEVELOPMENT_FLIGHT_ID` in the existing `microsoft-store`
   environment to the flight GUID. This is not the numeric submission ID in the
   portal URL. Use the Store API's
   [list flights](https://learn.microsoft.com/en-us/windows/uwp/monetize/get-flights-for-an-app)
   response to verify the GUID, product and group.
4. Finish the portal-created initial draft with a qualified Store bundle before
   using automation, or explicitly discard that draft. The publisher refuses
   to adopt or delete a pre-existing draft, including an empty initial one.
5. After this workflow lands on main, manually dispatch **Submit Microsoft
   Store**, specifying the eligible release `run_id` and `channel=development`.
   Selecting a stable run for Development, or a preview run for production, fails.
6. Verify certification, flight publication and a tester's actual Store
   install/upgrade. Then set the **repository** variable
   `MSSTORE_DEVELOPMENT_ENABLED=true`. Leave it unset or `false` until that proof.
   Production's `MSSTORE_ENABLED` remains independent.

Testers install/update the normal Store listing while signed into the enrolled
Microsoft account; there is no separate public preview listing. Enrollment and
certification can take time. Leaving a flight does not downgrade an installed
newer package: wait for production to catch up or follow the documented
data-preserving recovery guidance for the product.

Both channels share the existing submission concurrency group and OIDC
application. No new credential or broader GitHub environment access is needed.
The evidence records `channel` and `flightId`. Flight submissions preserve the
flight's publishing settings, replace only its packages, and do not edit the
production listing or its draft. A first API-created flight submission preserves
the new draft's defaults when no published flight baseline exists.

## Source binding

The source must be the canonical main release workflow's successful manual
dispatch. Its `openclaw-gateway-msix-store-submission-<attempt>` bundle and
`store-release-source-<attempt>` witness must both be unexpired. The witness
binds the run, current attempt, packaging commit, release channel, exact bundle
filename and SHA-256. Old runs without this witness are not eligible.
Development additionally requires the matching published GitHub prerelease and
tag commit; run `official` mode with `publish_release=true`. Stable Store-only
dispatches remain eligible for production. Signed sideload bundles are never
Store inputs.

### Store package versions

Store packages use `year.month.(10000 + github.run_number).0` for both stable
and preview releases. The year and month come from the captured release
identity, not the payload compatibility pin or wall clock. The offset exceeds
all versions from the previous scheme (third component at most 9999). The
workflow run number orders releases within a month; retries retain the same
number and immutable source snapshot. A previous month's stable release can
remain below a next-month flight; leaving the flight does not downgrade it.

Run numbers must be 1 through 55534. Exhaustion fails explicitly; do not reset,
wrap, or rename the release workflow to reset its counter. A future allocation
migration must preserve installed-package ordering. Independent Store and
sideload composition keeps each binary's compiled CLI version consistent with
its manifest. GitHub sideload version numbering does not change.
