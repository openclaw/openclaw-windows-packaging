# OpenClaw Windows MSIX

This repository builds a Windows MSIX package containing:

- one .NET 10 NativeAOT launcher exposed through the `openclaw` and `clawctl`
  app execution aliases;
- a build of [`openclaw/openclaw`](https://github.com/openclaw/openclaw)
  selected from upstream extended stable and pinned to one verified source
  commit for the workflow run;
- the official Node.js archive matching the upstream build's runtime version
  and the package architecture.

The package is independent from the
[OpenClaw Windows Node and Companion](https://github.com/openclaw/openclaw-windows-node)
and uses a separate `OpenClaw.Gateway` package identity. Both packages use the
OpenClaw Foundation publisher metadata established for OpenClaw's Windows
packages.

## Command model

Both aliases activate the same packaged `openclaw.exe`. The launcher recovers
the alias used to start it from the native process command line and selects one
of two deliberately separate surfaces.

### `openclaw`

`openclaw` is a transparent launcher for the bundled OpenClaw CLI. It does not
own package-management commands. Every argument, including an empty argument
list, is forwarded unchanged to `node openclaw.mjs`, and the launcher returns
the exact child exit code.

Before launching, the host resolves the bundled Node.js executable previously
prepared by `clawctl setup` and checks its PE product version and executable
architecture against the packaged archive without a separate Node.js process.
The runtime directory is prepended
to the child's `PATH` so Node.js, npm, and npx subprocesses use the bundled
tools without changing the user's environment.

The expanded OpenClaw application is installed read-only inside the MSIX.
After resolving Node.js, the launcher confirms that packaged
`app\openclaw.mjs` exists and executes it directly. It does not extract, hash,
copy, repair, or otherwise change package files at runtime.

Every OpenClaw child process runs with
`OPENCLAW_SUPERVISOR_MODE=external`,
`OPENCLAW_SERVICE_REPAIR_POLICY=external`, and
`OPENCLAW_NO_AUTO_UPDATE=1`. These declare external lifecycle ownership,
prevent doctor-owned service repair, and disable configured background
auto-updates. Handling of external supervisor mode, including refusal of native
service mutation and self-update, belongs to the selected upstream OpenClaw
version; the launcher does not reserve, reject, or rewrite upstream command
arguments. Following a channel does not enable OpenClaw's own updater inside
the read-only MSIX.
OpenClaw inherits the terminal's working directory; the launcher does not make
the read-only application directory the workspace.

### `clawctl`

`clawctl` exposes package readiness and launcher version information:

| Command | Behavior |
|---|---|
| `clawctl setup` | Extract the bundled Node.js runtime when needed and confirm packaged `app\openclaw.mjs` exists. |
| `clawctl --version` | Print the packaged launcher version. |

Bare `clawctl`, `clawctl -h`, and `clawctl --help` print help without changing
state. `clawctl setup --help` prints help for that command alone. Help, usage,
and completion come from
[System.CommandLine](https://learn.microsoft.com/en-us/dotnet/standard/commandline/).
Invalid management input is rejected with exit code `1` and a parse diagnostic
on standard error; no readiness check runs.

Help and version requests take precedence over the rest of the command line.
`clawctl --version bogus` prints the launcher version and exits `0` rather than
reporting `bogus`, because the version request is satisfied before the
remaining arguments are validated. The version printed is always the packaged
launcher's assembly version, including when the launcher is hosted by another
process.

Response-file expansion is disabled. A leading `@` has no meaning to `clawctl`
and is reported as an unrecognized argument rather than read from disk.

These parser conveniences belong to `clawctl` only. `openclaw` forwards every
argument to the OpenClaw CLI verbatim, so a leading `@` or a directive-shaped
token reaches that CLI uninterpreted.

Commands such as `doctor`, `gateway`, and `uninstall` belong to the OpenClaw
CLI and must be invoked through `openclaw`.

`setup` extracts the architecture-specific runtime archive from the immutable
MSIX into the package's writable LocalState:
`%LOCALAPPDATA%\Packages\<package-family>\LocalState\OpenClaw\NodeJS\node-v<version>-win-<architecture>`.
Extraction is idempotent, versioned, and serialized across concurrent setup
processes, including different Windows sessions. Setup validates existing
runtimes before reuse, replaces invalid runtimes, and validates extraction
before publishing it.

The launcher places Node.js in a Windows job configured with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. The launcher remains alive while Node.js
runs; if the launcher exits or is terminated, Windows terminates Node.js and
its child processes when the job handle closes.

Prepare the bundled runtime once, then use `openclaw`:

```powershell
clawctl setup
openclaw
```

When an MSIX update changes the bundled Node.js version, run `clawctl setup`
again before launching OpenClaw. Previously extracted versions are left in
place so an update does not remove a running process's runtime.

## Selecting the OpenClaw revision

Each new `.github\workflows\gateway-msix.yml` run resolves the channel named in
`release-policy.json`, currently `extended-stable`. This applies to
pull-request, `main` push, and manual runs, including official signing.
The channel is the public npm `openclaw@extended-stable` dist-tag, not a Git
branch or GitHub's latest release. The resolver selects its exact published
package version, resolves the matching signed `v<version>` upstream tag to an
immutable commit, and verifies the registry and source package identities
agree. Missing channels, unverified tags, and inconsistent metadata fail the
build; there is no fallback to `latest`, `beta`, or `main`.

The dedicated resolver job saves `source-resolution.json` before building.
All source, x64, ARM64, and bundle jobs use that snapshot, even if upstream
advances the channel while the run is in progress. Retries restore the original
snapshot and MSIX version rather than querying the channel again. The snapshot
artifact is retained for 90 days. If it was never uploaded or is no longer
available, start a new workflow run instead of retrying. Other build artifacts
remain short-lived.

For a one-time unsigned or test-signed override, run **Build OpenClaw Gateway
MSIX** manually and provide a tag, branch, or preferably a full 40-character
commit SHA in `openclaw_ref`. Leave this input empty to follow the policy.
Official signing rejects all explicit overrides, even a SHA that currently
matches the channel. No scheduled polling or upstream-triggered builds are
added: the channel is queried when a new CI run resolves its source.

The source build uses that revision's `.github/actions/setup-node-env` action
to select Node.js and pnpm. Its resolved Node.js version is recorded in
`source.json`, reused for both Windows payload builds, and carried in
`payload-metadata.json`. Package composition downloads that exact version;
the launcher derives its runtime version and LocalState path from the bundled
archive name. There is no separate packaging-side Node.js version pin or
runtime-support policy.

Build validation also checks the root package and generated Gateway provenance
against the resolved version and full source commit. Newer upstream builds
must share their explicit `buildId` with the Control UI service worker and
client assets. Older extended-stable builds that do not emit that field use
upstream's legacy UI identity (version plus the 12-character source commit).
Both formats are checked; missing provenance, invalid explicit IDs, and stale
or mismatched UI assets fail rather than bypassing validation.

All workflow jobs use GitHub's native `cache-mode: none`, which denies cache
reads and writes at the token boundary. This includes upstream source builds,
Windows npm lifecycle scripts, and manual ref overrides running from `main`.
The upstream setup action's cache options and the .NET cache opt-ins
are also disabled; these options avoid unnecessary cache calls, but are not
the security boundary. Upstream still selects Node.js and pnpm.

CodeQL's current `actions/cache-poisoning/poisonable-step` query
(`actions-queries` 0.6.35) does not model `cache-mode`. It can therefore still
flag upstream execution on `workflow_dispatch` even with native cache access
denied. Those findings need review against the enforced cache policy; this
repository does not suppress the query or obscure the upstream checkout.

The snapshot records the selector, exact upstream version, source commit,
release tag and tag-object identity, resolution time, and npm package integrity,
plus the packaging commit and workflow identity. Its SHA-256 is passed directly
from the resolver job to source-build and signing authorization jobs.
`source.json`, `payload-metadata.json`, and `msix-metadata.json` preserve the
source selection fields; MSIX metadata also records the packaging commit.
These build metadata files are not embedded in the MSIX. Embedded
`payload-files.json` records every packaged application file's path, length,
and SHA-256. The npm integrity identifies the published selection: the workflow
still rebuilds source, rather than claiming its new tarball is byte-identical
to npm's tarball.

`release-policy.json` approves the upstream repository and channel, the
publisher, and a packaging-only revision number. It no longer approves one
fixed OpenClaw commit: advancing the trusted upstream channel automatically
selects the next source for official releases without a packaging-repository
pin update. Changing this trust policy requires a reviewed repository change.
Official signing remains manual, runs only from `main`, and uses the protected
`release-signing` environment. Before requesting Azure credentials,
authorization checks the resolver's snapshot hash and workflow identity, both
architecture metadata files and MSIX hashes, the embedded manifests and
OpenClaw version, and every inventoried application file. It also byte-compares
the bundle's embedded packages with the authorized standalone packages.

## Build and test

```powershell
dotnet restore .\OpenClaw.Gateway.MSIX.slnx
dotnet test .\OpenClaw.Gateway.MSIX.slnx `
  --configuration Release `
  --no-restore
```

`scripts\Build-Payload.ps1` npm-installs an OpenClaw package into an expanded,
architecture-specific application tree. `scripts\Build-MSIX.ps1` downloads
the official Node.js archive matching the payload's recorded build version
and architecture, copies both inputs into package content, rejects Node.js
inside the application payload, creates a per-file inventory, and then creates
an unsigned NativeAOT MSIX.
`scripts\Build-LocalMSIX.ps1` can reuse a successful workflow payload or a
local payload directory. `-NodeArchivePath` can supply an already-downloaded
archive, but its version and architecture must match the payload metadata.

### Running a local development build

To go from a clean checkout to a registered, runnable package:

```powershell
.\scripts\Deploy-LocalPackage.ps1
```

This is the development inner loop. It does not build, sign, or install an
MSIX. It acquires the payload and the bundled Node.js runtime, publishes the
NativeAOT launcher, assembles a Developer Mode layout under
`artifacts\local-package`, registers it with `Add-AppxPackage -Register`, and
runs `clawctl setup` so `openclaw` is immediately usable.

The command is idempotent: re-running with nothing changed reports that the
package is already up to date and does nothing, and re-running after a source
or payload change rebuilds only what changed. The expanded application is
linked into the layout rather than copied, so repeat runs neither re-download
nor duplicate hundreds of megabytes.

| Option | Behavior |
| --- | --- |
| `-RefreshPayload` | Download the payload again; the previous one is kept until the new one registers successfully |
| `-PayloadRunId <id>` | Use a specific successful workflow run, reusing a matching cached payload |
| `-PayloadDirectory <path>` | Read a prepared payload directly, with no GitHub access and no modification; pass it on every run |
| `-Architecture x64` / `arm64` | Select the architecture; it must be runnable on this device |
| `-ReplaceExistingInstall` | Remove a conflicting MSIX-installed package first (see below) |
| `-SkipSetup` | Register without extracting the Node.js runtime |
| `-Force` | Re-register even when nothing changed |
| `-Unregister` | Remove the local registration, preserving app data and caches |

**Requires Developer Mode**, which the script checks before doing any work.

**It cannot coexist with an MSIX-installed `OpenClaw.Gateway`.** Windows
refuses to replace a packaged install with a local layout, and it cannot
preserve that package's app data across the switch, so the script stops and
explains rather than removing anything implicitly. Pass
`-ReplaceExistingInstall` to accept that trade.

**Run `-Unregister` before installing a released package.** Windows will not
replace a loose registration with a packaged install: `Add-AppxPackage` fails
with `0x80073CFB`, reporting that an unpackaged version is already installed
and a packaged version cannot replace it. This is the same mutual exclusion as
above, in the other direction, and it applies regardless of version. Unregister
first, then install the release:

```powershell
.\scripts\Deploy-LocalPackage.ps1 -Unregister
Add-AppxPackage -Path .\OpenClawGateway-0.0.0.0-x64.msix
```

**The registered package reads its files from the repository.** Deleting
`artifacts\local-package`, moving the checkout, or deleting the worktree breaks
the registration until the command runs again; `-Unregister` first if you plan
to remove the checkout. Local builds are unsigned development artifacts and are
never official-signing inputs.

Normal pull-request and push workflows publish unsigned packages for
validation. Manual runs support three signing modes:

- `unsigned` follows the policy channel unless an OpenClaw branch, tag, or commit
  override is supplied, and publishes unsigned MSIX packages;
- `test` follows the same source-selection rules and publishes MSIX packages signed with a
  temporary self-signed certificate plus the public `.cer` needed for local
  installation;
- `official` automatically authorizes the verified channel snapshot, rejects
  source overrides, may run only from `main`, and publishes signed packages
  as permanent GitHub Release assets.

Official signing uses the protected `release-signing` environment, Azure OIDC,
and the existing OpenClaw Artifact Signing account and certificate profile.
Test-signing private keys are generated only on the temporary GitHub runner
and are deleted before artifacts are uploaded. No signing secret or private
key is stored in the repository.

Official releases map upstream `YYYY.M.P` to MSIX
`YYYY.M.P.<packageRevision>` and repository release tag
`vYYYY.M.P.<packageRevision>`. For example, upstream `2026.6.35` with policy
`packageRevision: 0` produces MSIX `2026.6.35.0` and tag `v2026.6.35.0`.
Increase `packageRevision` through review for a packaging-only correction of
the same upstream source. A new upstream version naturally orders above the
previous version; the revision need not be reset. All components must fit the
existing package-version limits. Unsigned and test-signed CI builds retain
their run-based `0.*` versions, with retries reusing the initial version.

Official runs are serialized and reject an existing or older four-part release
tag before requesting signing credentials and again before publication,
including when either job is retried independently. A channel rollback therefore does
not silently publish a downgrade, and rebuilding the same version cannot
overwrite a release. A partial publication that already created its tag
requires deliberate recovery or a reviewed packaging revision, not automatic
republishing. The workflow creates the tag in this repository and a GitHub
Release with generated release notes and the exact upstream version/commit.
Each release
contains a signed, multi-architecture
`OpenClawGateway-<version>.msixbundle` as the recommended download, plus signed
`OpenClawGateway-<version>-x64.msix` and
`OpenClawGateway-<version>-arm64.msix` packages for architecture-specific
deployment, plus the source snapshot and both architecture metadata files for
permanent provenance. The bundle and standalone packages have the same
four-part identity version. The historical `0.0.0.0` signing proof is not used
for channel-based releases.

An `.msixbundle` is a single installable container for the x64 and ARM64 MSIX
packages; Windows selects the package appropriate for the device. An
`.appinstaller` file is separate update-channel metadata rather than an
alternative package format. This repository does not publish one yet, so GitHub
Release installs do not opt devices into automatic update checks.

### Official signing setup

The `release-signing` GitHub environment must define these environment
variables (they are identifiers, not credentials):

- `AZURE_CLIENT_ID`: application (client) ID of the dedicated
  `openclaw-windows-msix-signing` Entra application;
- `AZURE_TENANT_ID`: Entra tenant ID;
- `AZURE_SUBSCRIPTION_ID`: Azure subscription containing the signing resource.

Do not create an `AZURE_CLIENT_SECRET`. The `sign-msix` job requests a
short-lived Azure token with GitHub OIDC. The Entra application must have a
federated identity credential with:

- issuer: `https://token.actions.githubusercontent.com`;
- subject:
  `repo:openclaw@252820863/openclaw-windows-packaging@1347889239:environment:release-signing`;
- audience: `api://AzureADTokenExchange`.

This repository was created after GitHub's immutable OIDC subject rollout, so
the subject includes the organization and repository IDs. The older mutable
`repo:openclaw/openclaw-windows-packaging:...` form will not match its tokens.

The service principal must have `Artifact Signing Certificate Profile Signer`
on the `openclaw` certificate profile (or a containing scope). The workflow
uses account `openclaw`, certificate profile `openclaw`, and endpoint
`https://eus.codesigning.azure.net/`. The expected public certificate subject
is recorded in `release-policy.json`.

## Installed data

| Data | Default path |
|---|---|
| OpenClaw application files | Read-only MSIX package `app` directory |
| Bundled Node.js archive | Read-only MSIX package `runtime` directory |
| Extracted Node.js runtime | `%LOCALAPPDATA%\Packages\<package-family>\LocalState\OpenClaw\NodeJS\node-v<version>-win-<architecture>` |
| OpenClaw configuration and user state | `%USERPROFILE%\.openclaw` |
| Launcher diagnostics | `%LOCALAPPDATA%\Packages\<package-family>\LocalState\OpenClawGatewayMSIX\Logs\openclaw.log` |

OpenClaw application files are owned and serviced by Windows as part of the
immutable MSIX installation. OpenClaw user state remains outside the package.
Updating or removing the MSIX does not automatically delete that state or stop
a running Gateway. Use OpenClaw's documented
[`openclaw uninstall`](https://docs.openclaw.ai/install/uninstall) flow before
removing the MSIX.

## Integrity and isolation boundary

The payload build emits an expanded npm-installed application tree.
`Build-MSIX.ps1` rejects Node.js from that tree, copies it into package content,
and records every application file's path, length, and SHA-256 in
`payload-files.json`. It separately validates and hashes the pinned Node.js
archive. Package construction verifies both inputs against the generated MSIX.
Official signing authorization repeats the application inventory and Node.js
archive validation before requesting signing credentials.

At runtime, Windows' MSIX package integrity and read-only enforcement remains
the trust boundary for the application and archive. `clawctl setup` extracts
the archive into versioned package LocalState; `openclaw` launches the packaged
`app\openclaw.mjs` directly with that extracted executable. Neither command
hashes or walks the expanded application inventory.

The longer-term design is to run the Gateway payload in a dedicated isolated
agent session rather than the interactive session where the human user is
logged in. This will provide a boundary similar in purpose to running the
Gateway in WSL, using the forthcoming isolated-session capabilities. That
isolation is not provided by the current MSIX implementation.

## Contributors

This is an independent public implementation in the OpenClaw ecosystem, informed
by the upstream OpenClaw and Windows Node projects rather than a source fork of
either repository. See [CONTRIBUTORS.md](CONTRIBUTORS.md) for acknowledgements
and links to the contributor histories.
