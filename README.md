# OpenClaw Windows MSIX

This repository builds a Windows MSIX package containing:

- one .NET 10 NativeAOT launcher exposed through separate packaged
  `openclaw` and `clawctl` application identities and app execution aliases;
- a pinned, verified build of
  [`openclaw/openclaw`](https://github.com/openclaw/openclaw);
- the official Node.js archive matching the upstream build's runtime version
  and the package architecture.

The package is independent from the
[OpenClaw Windows Node and Companion](https://github.com/openclaw/openclaw-windows-node)
and uses a separate `OpenClaw.Gateway` package identity. Both packages use the
OpenClaw Foundation publisher metadata established for OpenClaw's Windows
packages.

## Requirements

- Windows 11 on a build that supports isolated agent sessions, on x64 or ARM64.
  OpenClaw always runs inside an isolated session, so a machine that cannot host
  one is not supported: `clawctl setup` and `openclaw` both fail with a message
  naming this requirement and the diagnostic log path. Install the latest
  Windows updates (Settings > Windows Update), or install a newer Windows
  version, and run `clawctl setup` again.
- Developer Mode is required only for the local loose-layout development loop,
  not for the signed MSIX.

## Command model

Both application identities activate the same packaged `openclaw.exe`. The
launcher recovers either the package-qualified application identity or the alias
used to start it from the native process command line and selects one of two
deliberately separate surfaces.

### `openclaw`

`openclaw` is a transparent launcher for the bundled OpenClaw CLI. It does not
own package-management commands. Every argument, including an empty argument
list, is forwarded unchanged to `node openclaw.mjs`, and the launcher returns
the exact child exit code.

`openclaw` runs only inside the isolated agent session recorded by
`clawctl setup`. It does not run OpenClaw on the host, and it fails rather than
falling back when the recorded session is missing, owned by another
installation, or unavailable.

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
`OPENCLAW_NO_AUTO_UPDATE=1`. It also reports the Windows Gateway session mode
through the process-stable `CLAWCTL_GATEWAY_ISOLATION=enabled` environment
variable. OpenClaw always runs inside the isolated session, so this value is
always `enabled`.
These values declare external lifecycle ownership, prevent doctor-owned service
repair, disable configured background auto-updates, and expose diagnostic
isolation status without claiming independent attestation. The selected OpenClaw runtime honors external supervisor mode by refusing native service
mutation and OpenClaw self-update with guidance to use the external supervisor's
workflow. This behavior belongs to upstream OpenClaw; the launcher does not
reserve, reject, or rewrite upstream command arguments.
OpenClaw inherits the terminal's working directory; the launcher does not make
the read-only application directory the workspace.

### `clawctl`

`clawctl` owns setup and the isolated-session operations:

| Command | Behavior |
|---|---|
| `clawctl setup` | Confirm packaged `app\openclaw.mjs` exists, provision or reuse the owned isolated session, and install the bundled Node.js runtime in the agent profile. It also configures gateway sign-in recovery without starting a gateway. On a machine that cannot host a session it fails with the Windows requirement described under [Requirements](#requirements). |
| `clawctl setup --fresh [--force]` | Remove this installation's owned session and package-local state, then run setup again. Without `--force`, incomplete external cleanup stops before local state is erased. `--force` is valid only with `--fresh`; it preserves an explicit warning when cleanup of owned external resources cannot be confirmed, but still stops if bounded local deletion fails. |
| `clawctl status` | Report the recorded isolated session, installed Node.js runtime, gateway, and sign-in recovery state without provisioning or replacing the session. It asks the backend to start the recorded provision as its status probe, so it is not a passive diagnostic. Use `clawctl gateway-service status` to inspect the gateway alone. |
| `clawctl teardown --force` | Confirm deletion, then stop and deprovision the owned session and remove its data and setup state. The MSIX remains installed. |
| `clawctl pwsh` | Open an interactive PowerShell session inside the agent session. |
| `clawctl collect-logs [--output <path>]` | Create a redacted host-and-agent diagnostics ZIP. |
| `clawctl gateway-service start` | Start the OpenClaw gateway in the isolated session and wait for it to listen. Requires setup. |
| `clawctl gateway-service status` | Inspect the gateway without starting it. |
| `clawctl gateway-service stop` | Stop the gateway while retaining the session and its data. |
| `clawctl --version` | Print the packaged launcher version. |

Bare `clawctl`, `clawctl -h`, and `clawctl --help` print help without changing
state. `clawctl setup --help` prints help for that command alone. Parsing,
usage errors, and completion come from
[System.CommandLine](https://learn.microsoft.com/en-us/dotnet/standard/commandline/),
while help is rendered by `clawctl` itself from the live command tree, so a
command added to the parser is documented without a separate help edit.
Invalid management input is rejected with exit code `1` and a parse diagnostic
on standard error; no readiness check runs.

All non-interactive commands accept `--json` and emit a versioned JSON document
on standard output. Human diagnostics remain on standard error, and command
exit codes do not change. `clawctl pwsh --json` is rejected because the command
hands the terminal to an interactive shell.

Interactive terminals use color for headings and status marks. `--no-color`,
the `NO_COLOR` environment variable, redirected output, and CI disable color;
`FORCE_COLOR` enables it for redirected output or CI unless color was
explicitly disabled. JSON output never contains terminal escape sequences.

### Starting the gateway

`clawctl gateway-service start` waits for the gateway to bind rather than
returning as soon as the process exists, because a process without a listener
is not a usable gateway. It reports each stage as it happens — a spinner on an
interactive terminal, one line per stage anywhere else — and narrates nothing
at all under `--json`, so standard output carries exactly one document.

The wait has a fixed budget. A gateway still coming up when the budget is spent
is reported as starting rather than failed, and `clawctl gateway-service status`
will show it once it binds.

`clawctl` reports the gateway port only when it can identify that listener
unambiguously:

```text
clawctl gateway-service start

  Gateway:    ✓ listening
  Port:       18789

  Token:      openclaw gateway auth-token --show
```

OpenClaw owns the endpoint configuration, including TLS and a custom Control UI
base path. The Windows package therefore does not construct an HTTP URL that
might contradict that configuration. It reports no port when multiple
unclassified listeners remain. JSON follows the same rule: `gateway.port` is
present only when identified, and no URL is promised.
Reaching the Control UI needs the shared gateway token, which
`openclaw gateway auth-token --show` reveals. `--json` carries the identified
port but not that command: a script should run it rather than parse a
suggestion.

Help and version requests take precedence over the rest of the command line.
`clawctl --version bogus` reports the build identity and exits `0` rather than
reporting `bogus`, because the version request is satisfied before the
remaining arguments are validated.

`clawctl --version` reports the package version and packaging-repository
commit alongside the bundled OpenClaw payload version and its commit:

```text
clawctl 0.0.0.1

  Package:   0.0.0.1 (bfcb5ba73e7ea3e88ceed8c326e58e5baadc3191)
  Payload:   2026.8.2 (0965053fe6b9341776df147a6934b7485c60b5ca)
```

Each commit is the one that produced the version it follows, and is muted so
the version stays the value a reader compares.

`clawctl --version --json` reports the same identity as a versioned document,
so a support or deployment script can collect it without parsing prose:

```json
{
  "ok": true,
  "schemaVersion": 1,
  "command": "version",
  "package": { "version": "0.0.0.1", "commit": "bfcb5ba…" },
  "payload": { "version": "2026.8.2", "commit": "0965053…" }
}
```

Those four values are compiled into the binary as constants by the build that
produces the package, so the report cannot drift from the payload it shipped
with and costs no file or process access at startup. `Build-MSIX.ps1` supplies
the versions and commits it also records in `msix-metadata.json`; an ordinary
build falls back to the pin recorded in `release-policy.json`, and reports
`unknown` for a value no build supplied. The report never comes from the entry
assembly, so it stays correct when the launcher is hosted by another process.

Response-file expansion is disabled. A leading `@` has no meaning to `clawctl`
and is reported as an unrecognized argument rather than read from disk.

These parser conveniences belong to `clawctl` only. `openclaw` forwards every
argument to the OpenClaw CLI verbatim, so a leading `@` or a directive-shaped
token reaches that CLI uninterpreted.

Commands such as `doctor`, `gateway`, and `uninstall` belong to the OpenClaw
CLI and must be invoked through `openclaw`.

`clawctl setup` provisions an explicitly owned agent session and extracts the
architecture-specific runtime archive from the immutable MSIX into that agent's
writable LocalState:
`%LOCALAPPDATA%\Packages\<package-family>\LocalState\OpenClaw\NodeJS\node-v<version>-win-<architecture>`.
Extraction is idempotent, versioned, and serialized across concurrent setup
processes, including different Windows sessions. Setup validates existing
runtimes before reuse, replaces invalid runtimes, and validates extraction
before publishing it. It does not prepare the invoking user's host runtime,
because nothing runs on the host.

Run setup before using `openclaw`, `clawctl pwsh`, or gateway-service start.
There is no session-free mode: `openclaw` runs inside the session recorded by
setup, and both entry points fail with the same message on a machine that
cannot host one. See
[MXC compatibility evidence](docs/mxc-compatibility-evidence.md) for the
session model, gateway health criteria, and diagnostics limits.

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

`.github\workflows\gateway-msix.yml` resolves an explicit OpenClaw ref before
building. Pull-request and `main` push runs use the pinned commit configured in
both:

- `workflow_dispatch.inputs.openclaw_ref.default`;
- the non-manual fallback in `env.OPENCLAW_REF`.

Changing only the workflow-dispatch default does not change automatic builds.
For a one-time override, run **Build OpenClaw Gateway MSIX** manually and
provide a tag, branch, or preferably a full 40-character commit SHA in
`openclaw_ref`. Payload composition validates that the selected OpenClaw
runtime discovers the packaging-owned Windows Launcher plugin in its
default-disabled state, then explicitly enables only that plugin in an isolated
temporary validation profile before using OpenClaw's runtime inspection pass to
validate its required read-only route shape. The temporary profile is removed
after inspection and does not modify user configuration. Incompatible older
refs fail instead of producing a package with an unvalidated plugin.

The source build uses that revision's `.github/actions/setup-node-env` action
to select Node.js and pnpm. Its resolved Node.js version is recorded in
`source.json`, reused for both Windows payload builds, and carried in
`payload-metadata.json`. Package composition downloads that exact version;
the launcher derives its runtime version and LocalState path from the bundled
archive name. There is no separate packaging-side Node.js version pin or
runtime-support policy.

Non-official workflows cache the packed OpenClaw tarball by its resolved
upstream commit. They also cache each architecture's Windows dependency tree by
the resolved commit, tarball SHA-256, Node.js version, and payload-build script.
A tarball cache hit still verifies the recorded commit and SHA-256; a
dependency-tree hit still runs every payload validation and smoke test.
Official-signing workflows bypass
both caches and always rebuild upstream source and Windows dependencies.

The payload artifact records the requested ref and resolved upstream commit in
`payload-metadata.json`. That build-only file is not embedded in the MSIX.
`msix-metadata.json` records both the packaging repository commit and bundled
OpenClaw commit, while embedded `payload-files.json` records every packaged
application file's path, length, and SHA-256.

`release-policy.json` records the immutable OpenClaw commit and Gateway tag
approved for official signing, plus an independent MSIX packaging revision.
Updating that
policy requires a reviewed repository change. Official signing runs only from
`main` and verifies the workflow input, policy-approved package version, both
architecture metadata files, both MSIX hashes, the embedded manifests, and
every file against the embedded application inventory. It also byte-compares
the bundle's embedded packages with those authorized standalone packages before
requesting Azure credentials.

## Build and test

```powershell
dotnet restore .\OpenClaw.Gateway.MSIX.slnx
dotnet test .\OpenClaw.Gateway.MSIX.slnx `
  --configuration Release `
  --no-restore
```

`scripts\Build-Payload.ps1` npm-installs an OpenClaw package into an expanded,
architecture-specific application tree. It validates the Gateway and Control UI
build identities on the installed tree, including reused staged installs, then
provisions the packaging-owned Windows Launcher plugin into the payload copy's
bundled plugin directory. Its internal package, path, and plugin ID remain
`gateway-isolation`. The plugin is disabled by default, so normal installs do
not activate it, register its route, or show the **Windows Launcher** tab. When
explicitly enabled for validation or by the future launcher command
implementation, it adds the read-only tab to the Control group and serves it
through an authenticated, sandboxed plugin route. It reads only the launch-time
`CLAWCTL_GATEWAY_ISOLATION` value and registers no mutation RPC or process
control.

The page preserves the planned `clawctl gateway-isolation enable|disable`
command and Copy control for the paired launcher command update. This package
does not register those `clawctl` commands yet, so the page explicitly tells
users to run the command only after that support is installed.

The screenshots attached to the pull request are design and behavior proof
captured with the plugin explicitly enabled in an isolated validation profile;
they do not represent the default-disabled state of a normal install.

Full selected-theme cohesion requires the generic plugin-frame theme forwarding
merged by
[`openclaw/openclaw#145409`](https://github.com/openclaw/openclaw/pull/145409).
The current workflow remains on the release-approved OpenClaw `v2026.9.4`
baseline (`3a9d69db306cd7f081e06254cb89c4bcc14a7107`) while this plugin is disabled by
default. That baseline packages and inspects the plugin safely but does not
forward selected Control UI themes into plugin frames. The future launcher
enablement change must also advance and qualify the runtime to the merged theme
forwarding commit `f65ecca89667b8a55d9f88d76c487f0a0ab11da8` or newer. Until
then, the page uses the browser or operating system light/dark preference with
a safe built-in palette.

`scripts\Build-MSIX.ps1` downloads the official Node.js archive matching the
payload's recorded build version and architecture, copies both inputs into
package content, rejects Node.js inside the application payload, creates a
per-file inventory, and then creates an unsigned NativeAOT MSIX.
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

- `unsigned` accepts any OpenClaw branch, tag, or commit and publishes unsigned
  MSIX packages;
- `test` accepts any OpenClaw ref and publishes MSIX packages signed with a
  temporary self-signed certificate plus the public `.cer` needed for local
  installation;
- `official` requires the approved immutable commit from
  `release-policy.json`, may run only from `main`, and publishes the signed
  packages as permanent assets on a GitHub Release named by the policy.

Official signing uses the protected `release-signing` environment, Azure OIDC,
and the existing OpenClaw Artifact Signing account and certificate profile.
Test-signing private keys are generated only on the temporary GitHub runner
and are deleted before artifacts are uploaded. No signing secret or private
key is stored in the repository.

Official releases derive their GitHub tag and four-part numeric MSIX identity
from `gatewayTag` and `msixRevision` in `release-policy.json`. The GitHub tag is
`<gateway-tag>-msix.<revision>`. The MSIX identity is
`year.month.patch.(gateway-release-sequence * 1000 + msix-revision)`.

| Gateway tag | MSIX revision | GitHub release tag | MSIX version |
|---|---:|---|---|
| `v2026.7.1` | `0` | `v2026.7.1-msix.0` | `2026.7.1.1000` |
| `v2026.7.1-2` | `0` | `v2026.7.1-2-msix.0` | `2026.7.1.2000` |
| `v2026.7.1-2` | `1` | `v2026.7.1-2-msix.1` | `2026.7.1.2001` |
| `v2026.7.2` | `0` | `v2026.7.2-msix.0` | `2026.7.2.1000` |

The unsuffixed Gateway tag is release sequence `1`; correction suffixes `-2`
through `-64` use their numeric suffix as the sequence. A `-1` suffix is
rejected because it would collide with the unsuffixed tag. Set `msixRevision`
from `0` through `999`, starting at `0` for each Gateway tag and incrementing it
only when that exact Gateway tag is repackaged. Each Gateway release therefore
owns a deterministic 1,000-number block, and an MSIX-only rebuild cannot shift
the version assigned to a later Gateway correction or patch.

To prepare an official release, update these policy inputs together in a
reviewed pull request:

1. `gatewayTag` to the stable upstream Gateway tag;
2. `approvedCommit` to the immutable commit resolved from that tag;
3. `payloadPackageVersion` to the version reported by the pinned payload;
4. `msixRevision` to `0`, or increment it for a packaging-only rebuild of the
   same Gateway tag;
5. the workflow's `openclaw_ref` default and non-manual fallback to the same
   `approvedCommit`.

After that pull request merges, manually run **Build OpenClaw Gateway MSIX** on
`main` with `openclaw_ref` set to the approved commit and `signing_mode` set to
`official`. The workflow derives the package version and release tag, creates
the tag in this repository, and publishes a GitHub Release with generated
release notes. Each release contains a signed, multi-architecture
`OpenClawGateway-<version>.msixbundle` as the recommended download, plus signed
`OpenClawGateway-<version>-x64.msix` and
`OpenClawGateway-<version>-arm64.msix` packages for architecture-specific
deployment. The duplicate GitHub Actions artifacts remain short-lived transport
and diagnostic copies.

Microsoft Store submissions reserve the fourth version component as zero, so
Store publication will need its own version policy when it is introduced.

The signed `v0.0.0.0` and `v0.0.0.1` proof releases are not production version
identities, but they are retained as transition baselines. Pull requests that
change release versioning download the hash-pinned standalone x64 and
recommended `.msixbundle` assets, install each one on a clean GitHub-hosted
Windows runner, upgrade it in place through the same delivery format, and
verify that the package family remains stable and a LocalState marker is
retained. The gate also proves fresh installation of both the standalone and
bundle candidates. It refuses to run when an OpenClaw Gateway package is
already registered and removes only packages installed by that test
invocation. It temporarily trusts the ephemeral test-signing certificate in
the local-machine Trusted People store, as required by Windows deployment, and
removes that certificate in `finally`. The resulting JSON evidence is retained
as a workflow artifact for 90 days. Future versioning schemes must keep this
transition gate green or explicitly document and obtain approval for a
breaking reset.

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
