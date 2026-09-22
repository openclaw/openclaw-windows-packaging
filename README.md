# OpenClaw Windows MSIX

This repository builds a Windows MSIX package containing:

- one .NET 10 NativeAOT launcher exposed through separate packaged
  `openclaw` and `clawctl` application identities and app execution aliases;
- a pinned, verified build of
  [`openclaw/openclaw`](https://github.com/openclaw/openclaw);
- the official Node.js archive matching the upstream build's runtime version
  and the package architecture.

Builds use .NET SDK 10.0.400 or a later .NET 10 feature band.

The package is independent from the
[OpenClaw Windows Node and Companion](https://github.com/openclaw/openclaw-windows-node)
and uses the Partner Center-reserved
`OpenClawFoundation.OpenClawGateway` package identity. Its package family is
`OpenClawFoundation.OpenClawGateway_rfcbke2p71se2` and its publisher is
`CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC`.

## Contributor guides

- [Agent instructions](AGENTS.md) define repository-wide working, ownership,
  validation, and safety rules and route scoped source, script, and test work.
- [Architecture](docs/architecture.md) explains the host, isolated session,
  MXC, agent runtime, and gateway boundaries.
- [Troubleshooting](docs/troubleshooting.md) maps observable failures to checks
  and recovery steps.
- [Local development](docs/local-development.md) covers loose registration,
  local MSIX composition, test signing, and the NativeAOT validation lanes.
- [Official release process](docs/release-process.md) covers the reviewed
  policy change, signing workflow, upgrade evidence, and publication checks.

## Requirements

- Windows 11 on a build that supports isolated agent sessions, on x64 or ARM64.
  OpenClaw always runs inside an isolated session, so a machine that cannot host
  one is not supported: both `clawctl setup` and `openclaw` fail with a message
  naming this requirement and the diagnostic log path. `openclaw` first
  attempts automatic setup. Install the latest Windows updates (Settings >
  Windows Update), or install a newer Windows version, and run `clawctl setup`
  again.
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

`openclaw` runs only inside an isolated agent session. When the setup marker is
absent, it provisions the installation before forwarding its arguments, so a
clean machine can start with `openclaw`, including `openclaw --help` and
`openclaw --version`. It does not run OpenClaw on the host. Unreadable,
incomplete, foreign, newer-schema, preparing, or tearing-down setup markers,
session mismatches, and stale agent Node.js runtimes remain explicit recovery
states with their `clawctl setup` or `clawctl teardown` guidance; automatic
setup never repairs them.

Before launching, the host resolves the bundled Node.js executable previously
prepared during setup and checks its PE product version and executable
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

After each successful interactive `openclaw` invocation, the launcher performs
a file-only readiness check inside the isolated session. If the default
`.openclaw\openclaw.json` has `gateway.mode` set to `local` and the managed
gateway is `NotStarted` or `Stopped`, it starts the gateway when readiness is
`startup eligible`, narrating progress on standard error. It does nothing for
a non-zero OpenClaw exit code, redirected output, non-eligible readiness, or a
`Starting`, `Unhealthy`, or `Unknown` gateway. An observed `Running` gateway is
acknowledged. A failed or unverified start writes a standard-error warning and
a retry command, `clawctl gateway-service start`, without changing the
OpenClaw exit code; the next eligible run retries. A successful start is
acknowledged for the current Windows logon. See [environment
variables](#environment-variables) to restore the prior start hint.

### Environment variables

These host-side variables are not passed to the OpenClaw child. Automatic
behavior is enabled by default. Set either variable to `0`, `false`, `no`, or
`off` (case-insensitive and with surrounding whitespace ignored) to suppress
its automatic behavior; any other value, including an unset variable, leaves
it enabled.

| Variable | Suppressed behavior |
| --- | --- |
| `CLAWCTL_AUTO_SETUP` | Restores the previous clean-machine failure: `openclaw` reports that it has not been set up and directs you to `clawctl setup`; it provisions, records, and registers nothing. |
| `CLAWCTL_AUTO_GATEWAY_START` | Restores the previous `clawctl gateway-service start` hint instead of automatically starting an eligible gateway. |

### `clawctl`

`clawctl` owns explicit setup, recovery, and the isolated-session operations:

| Command | Behavior |
|---|---|
| `clawctl setup` | Confirm packaged `app\openclaw.mjs` exists, provision or reuse the owned isolated session, and install the bundled Node.js runtime in the agent profile. It also configures gateway sign-in recovery without starting a gateway. On a machine that cannot host a session it fails with the Windows requirement described under [Requirements](#requirements). |
| `clawctl completion` | Write PowerShell completion for both `clawctl` and `openclaw` to standard output. Source it for the current shell, or use `--install` to add a marked block to the current-user PowerShell profile. |
| `clawctl completion --install [--profile <path>]` | Atomically update the selected profile (or the current-user PowerShell profile) with a marked loader that sources completion from the currently installed `clawctl` package whenever a new shell starts. Package updates therefore take effect without rewriting the profile. The trusted upstream script is also cached in host LocalState, and `clawctl pwsh` safely projects it into the current isolated workspace. `--uninstall` removes the marked profile block and invalidates the host cache so later agent shells do not load a stale projection. |
| `clawctl setup --fresh [--force]` | Remove this installation's owned session and package-local state, then run setup again. Without `--force`, incomplete external cleanup stops before local state is erased. `--force` is valid only with `--fresh`; it preserves an explicit warning when cleanup of owned external resources cannot be confirmed, but still stops if bounded local deletion fails. |
| `clawctl status` | Report the recorded isolated session, agent account and shared folder, installed Node.js runtime, gateway, sign-in recovery, and file-only config readiness when the gateway is not running. It asks the backend to start the recorded provision as its status probe, so it is not a passive diagnostic, but it does not provision a replacement or start the gateway. Use `clawctl gateway-service status` to inspect the gateway alone. |
| `clawctl open` | Open the running managed gateway's Control UI in the default browser. Requires completed `clawctl setup` and an already-running gateway; it probes those prerequisites and fails rather than starting the gateway. Packaged OpenClaw resolves the endpoint, TLS, Control UI base path, and authenticated one-time browser handoff. Authenticated URLs and tokens are not printed. |
| `clawctl teardown --force` | Confirm deletion, then stop and deprovision the owned session and remove its data and setup state. The MSIX remains installed. |
| `clawctl pwsh` | Open an interactive PowerShell session inside the agent session. |
| `clawctl pwsh --command <text>` | Run one quoted PowerShell command string inside the agent session and return its exit code. |
| `clawctl pwsh --file <path> [-- <arguments>]` | Run an agent-visible PowerShell script and return its exit code. Relative paths start in the shared folder reported by `clawctl status`; use `--` before script arguments that begin with `-`. |
| `clawctl collect-logs [--output <path>]` | Create a redacted host-and-agent diagnostics ZIP. |
| `clawctl gateway-service start` | Start the OpenClaw gateway in the isolated session and wait for it to listen. Requires setup. |
| `clawctl gateway-service status` | Inspect the gateway without starting it. When the gateway is not running, it may start/probe only the already-recorded isolated session to report file-only config readiness; it never provisions a replacement or starts the gateway. |
| `clawctl gateway-service stop` | Stop the gateway while retaining the session and its data. |
| `clawctl gateway-service restart` | Stop the gateway and start it again as one lifecycle operation. If the stop cannot be verified, it retains the gateway record and does not start a replacement. If no gateway is running, it starts one. |
| `clawctl --version` | Print the packaged launcher version. |

Bare `clawctl`, `clawctl -h`, and `clawctl --help` print help without changing
state. `clawctl setup --help` prints help for that command alone. Parsing,
usage errors, and completion come from
[System.CommandLine](https://learn.microsoft.com/en-us/dotnet/standard/commandline/),
while help is rendered by `clawctl` itself from the live command tree, so a
command added to the parser is documented without a separate help edit.
Invalid management input is rejected with exit code `1` and a parse diagnostic
on standard error; no readiness check runs.

All commands except `pwsh` accept `--json` and emit a versioned JSON document
on standard output. Human diagnostics remain on standard error, and command
exit codes do not change. Every `clawctl pwsh` mode preserves PowerShell's
standard streams, so `--json` is rejected rather than capturing or wrapping
script output.

Quote inline PowerShell as one `--command` value:

```powershell
clawctl pwsh --command 'Get-Content ~/foo.txt'
clawctl pwsh --file .\diagnose.ps1 -- -Detailed -Name 'test value'
```

PowerShell evaluates the inline command inside the isolated agent, so `~`
within `--command` names the agent profile rather than the invoking user's
profile. A relative `--file` path starts in the recorded shared folder;
`clawctl` does not copy a host-local script into that folder.

When the gateway is not confirmed running, status JSON includes an optional
`gateway.readiness` object with `state`, stable `reason`, and failure `detail`
where applicable. The readiness states are `absent`, `not-ready`,
`startup-eligible`, `unavailable`, and `unknown`. A running gateway omits this
object and incurs no config-readiness probe.

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
```

OpenClaw owns the endpoint configuration, including TLS and a custom Control UI
base path. `clawctl status` and `clawctl gateway-service start` therefore do
not construct an HTTP URL that might contradict that configuration. They report
no port when multiple unclassified listeners remain; JSON follows the same
rule, with `gateway.port` present only when identified and no URL promised.

`clawctl open` uses packaged OpenClaw's verified, authenticated browser handoff
instead of constructing a URL from that port. It does not start or recover the
gateway: start it first with `clawctl gateway-service start` if the probe says
it is not running. The command does not print authenticated URLs or tokens.

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

Setup also mirrors the application's native dependency packages into the
agent's own LocalState, under
`%LOCALAPPDATA%\OpenClawGatewayMSIX\agent-native\<content-id>`. The
isolated-session identity may read packaged files but may not map them as
executable images, so loading a `.node` addon directly from the package fails
with `ERR_DLOPEN_FAILED` even though the same bytes load from a writable
location. Only the packages that carry a `.node`, `.dll`, or `.exe` artifact
are mirrored; the rest of the application, which is nearly all of it, keeps
executing from the immutable package.

That set is discovered by scanning `app\node_modules`, never from a hard-coded
list, so an upstream revision that introduces a new native dependency is staged
automatically. Whole owning package directories are copied rather than
individual binaries, because a package locates its sibling libraries and helper
executables relative to its own directory. A packaged preload then redirects
both CommonJS and ESM resolution to the staged copies. The launcher places that
preload on the agent Node.js argument vector so OpenClaw retains it when an
agent invokes `openclaw` again. Once loaded, the preload appends itself to the
agent account's own `NODE_OPTIONS` for ordinary Node.js workers, so an option
the agent set survives and the invoking host's value never reaches it. Staging
is idempotent, keyed by package content, and reclaims the superseded copy after
an upgrade once nothing is still running from it; a launch holds its root for
its whole lifetime, and a root that is still held is left whole for a later
setup.

Run `clawctl setup` before using `clawctl pwsh` or gateway-service start.
`openclaw` provisions a clean installation automatically, but there is no
session-free mode: it runs only inside the recorded session and leaves degraded
setup state to explicit recovery. Both setup paths fail with the same message
on a machine that cannot host a session. See
[MXC compatibility evidence](docs/mxc-compatibility-evidence.md) for the
session model, gateway health criteria, and diagnostics limits.

The launcher places Node.js in a Windows job configured with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. The launcher remains alive while Node.js
runs; if the launcher exits or is terminated, Windows terminates Node.js and
its child processes when the job handle closes.

On a clean installation, start directly with `openclaw`:

```powershell
openclaw
```

Run `clawctl setup` first when you want the explicit setup, recovery, or
`--fresh` path.

When an MSIX update changes the bundled Node.js version, run `clawctl setup`
before launching OpenClaw. Automatic setup does not repair a stale agent Node.js
runtime after an update. Previously extracted versions are left in place so an
update does not remove a running process's runtime.

## Selecting the OpenClaw revision

`.github\workflows\gateway-msix.yml` selects **stable** through public npm
`openclaw@latest` whenever a new packaging run starts. The resolver checks the
exact published version, its signed upstream tag and commit, and the source
package version before building. There is no automatic fallback to another
version or channel; extended-stable and named prereleases are rejected.
Source selection also checks the MSIX release-version rules before building:
numeric correction suffixes must be `-2` through `-9`. Unsupported corrections
are rejected for channel selection, explicit refs, policy pins, and retries.

The `openclaw-source-resolution` artifact records this choice once per run.
Retries reuse it without querying the moving channel again. If the snapshot
is missing or expired (90-day retention), start a new run instead of retrying.
Package and payload metadata record the resolved source commit and version.

For a one-time unsigned/test override, provide a stable-source tag, branch, or
full commit SHA in the manual `openclaw_ref` input. Empty means follow stable.
If compatibility requires an older known-good stable release, a reviewed
`stableVersion` field in `release-policy.json` can pin its exact version, for
example `"stableVersion": "2026.9.4"`. A pin is not automatic fallback and does
not grant official-signing approval.

For official signing, the selected source must match `approvedCommit`,
`gatewayTag`, and `payloadPackageVersion` in `release-policy.json`. An empty
input selects stable and checks that approval; an explicit input must be the
full approved commit SHA.

Payload composition validates that the selected OpenClaw runtime discovers and
activates the packaging-owned Windows Launcher plugin by default, both without
configuration and with an existing profile that has no plugin decision. Runtime
inspection verifies its read-only route shape without writing enablement or
allowlist overrides, then verifies that an explicit disable prevents registration.
The isolated temporary profile is removed after inspection and does not modify
user configuration. Incompatible older
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
A tarball cache hit still verifies the recorded version, commit and SHA-256; a
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
architecture-specific application tree. Run it on Windows with Node.js matching both
the selected upstream version and target architecture: native install scripts
can use `process.arch` instead of npm's target-CPU flag. CI builds x64 on
`windows-latest` and ARM64 on `windows-11-arm`, using matching Node.js binaries.
Both payloads run their CLI smoke test. Cross-architecture Node.js execution
is rejected before staging or npm installation, including when reusing a tree.
These jobs validate payload loading and packaging, not installed agent-session
E2E on a supported host. `Build-MSIX.ps1` and
`Build-LocalMSIX.ps1 -PayloadDirectory` can still cross-compose an already-qualified
payload.
It validates the Gateway and Control UI
build identities on the installed tree, including reused staged installs, then
provisions the packaging-owned Windows Launcher plugin into the payload copy's
bundled plugin directory. Its internal package, path, and plugin ID remain
`gateway-isolation`. The bundled plugin is enabled by default and adds the
read-only **Windows Launcher** tab to the Control group, serving it
through an authenticated, sandboxed plugin route. It reads only the launch-time
`CLAWCTL_GATEWAY_ISOLATION` value and registers no mutation RPC or process
control. The informational page shows one **Gateway Isolation** row with an
**Active** badge only for the exact `enabled` report (HTTP 200). Missing, malformed,
or unsupported reports, including `disabled`, show **Invalid**
and neutral status-unavailable text (HTTP 503), never a supported off state.
The live route establishes Gateway availability; disconnected-Gateway messaging
belongs to the Control UI. There are no isolation controls.

The active page's **Command reference** section provides a brief, copy-only
cheat sheet grouped under **ClawCtl** and **OpenClaw**. Enter these commands
in your normal Windows terminal (user session):

- **ClawCtl:** `clawctl gateway-service status`, `clawctl gateway-service restart`,
  `clawctl open`, `clawctl pwsh`, and
  `clawctl --help`. PowerShell opens inside the isolated agent, where `openclaw`
  and `node` are available; ClawCtl manages the session from outside it.
  Restart preserves the session and its data and starts the Gateway if it is
  not running. An unverified stop prevents a replacement from starting.
  **Open dashboard** opens the default browser when you run `clawctl open`;
  it requires completed setup and a running Gateway, and does not print
  authenticated URLs or tokens.
- **OpenClaw:** `openclaw tui` and `openclaw --help`. The packaged `openclaw`
  command forwards to your agent session; inside `clawctl pwsh`, it runs directly.

The page never executes commands or sends mutation requests. Copy controls
announce success only after a clipboard operation succeeds; otherwise they
offer manual-copy guidance, leaving the command selected when possible.
Invalid isolation reports show no command references.

The report is captured once at plugin creation. It is a launcher-provided
diagnostic, not independent isolation attestation. The launcher now requires an
isolated session and supplies `CLAWCTL_GATEWAY_ISOLATION=enabled` to its guest
processes. It no longer supports host execution or the old `OPENCLAW_SESSION`
routing preference; that variable is not accepted as a substitute report here.
The default enables the tab, not isolation, and does not alter launcher execution.

Existing profiles without a plugin decision adopt the new default on the next
normal start of the updated Gateway. No configuration migration or automatic
restart is added. Explicit user disables, global plugin disablement, denylists,
and restrictive allowlists retain OpenClaw's standard precedence; the package
does not rewrite them. Direct-Node UI fixtures verify rendering and interactions,
not packaged-launcher isolation or installed upgrade behavior.

Full selected-theme cohesion requires the generic plugin-frame theme forwarding
merged by
[`openclaw/openclaw#145409`](https://github.com/openclaw/openclaw/pull/145409).
OpenClaw `v2026.9.5` at `ec9c1a13db8938e5a3eaa51fca2e981cde2395a9`
includes that forwarding and has been qualified with this default-on plugin.
Unsigned and test builds follow the stable-source selection above; that
qualification is not an official runtime approval.
The official-signing policy remains on the release-approved OpenClaw `v2026.9.4`
baseline (`3a9d69db306cd7f081e06254cb89c4bcc14a7107`); that approval does not
establish support for this default-on/theme contract. A compatible runtime
requires separate reviewed approval before an official release. Without theme
forwarding, the page uses the browser or operating system light/dark preference
with a safe built-in palette.

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

**It cannot coexist with an MSIX-installed
`OpenClawFoundation.OpenClawGateway`.** Windows
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
validation. Manual runs support four modes:

- `unsigned` follows stable or a stable-source override and publishes unsigned
  MSIX packages;
- `test` uses the same source-selection rules and publishes MSIX packages signed with a
  temporary self-signed certificate plus the public `.cer` needed for local
  installation;
- `store` requires the reviewed immutable commit from `release-policy.json`,
  may run only from `main`, and publishes permanent unsigned Partner Center
  submission assets; Microsoft signs them during Store ingestion;
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
`year.month.VVPN.0`: `VV` is the two-digit monthly Gateway release sequence,
`P` is the Gateway correction digit, and `N` is the MSIX rebuild digit. The
digits are packed numerically into the third component, so leading zeroes are
not written.

| Gateway tag | MSIX revision | GitHub release tag | MSIX version |
|---|---:|---|---|
| `v2026.7.1` | `0` | `v2026.7.1-msix.0` | `2026.7.100.0` |
| `v2026.7.1` | `1` | `v2026.7.1-msix.1` | `2026.7.101.0` |
| `v2026.7.1-2` | `0` | `v2026.7.1-2-msix.0` | `2026.7.120.0` |
| `v2026.7.2` | `0` | `v2026.7.2-msix.0` | `2026.7.200.0` |
| `v2026.7.12` | `0` | `v2026.7.12-msix.0` | `2026.7.1200.0` |

Gateway release sequences must be `1` through `99`. The unsuffixed Gateway tag
uses correction digit `0`; correction suffixes `-2` through `-9` use their
numeric suffix. A `-1` suffix remains rejected to match the Gateway release-tag
contract. Set `msixRevision` from `0` through `9`, starting at `0` for each
Gateway tag and incrementing it only when that exact Gateway tag is repackaged.
Decimal place value guarantees Gateway release > Gateway correction > MSIX
rebuild while keeping every component at four digits or fewer and reserving the
fourth component as `0` for Microsoft Store submission.

To prepare an official release, update these policy inputs together in a
reviewed pull request:

1. `gatewayTag` to the stable upstream Gateway tag;
2. `approvedCommit` to the immutable commit resolved from that tag;
3. `payloadPackageVersion` to the version reported by the pinned payload;
4. `msixRevision` to `0`, or increment it for a packaging-only rebuild of the
   same Gateway tag.

After that pull request merges, manually run **Build OpenClaw Gateway MSIX** on
`main` with `openclaw_ref` set to the approved commit. Use `signing_mode=store`
for the Partner Center identity, or `signing_mode=official` only when the Azure
certificate subject exactly matches the reviewed publisher. The workflow derives
the package version and release tag, creates
the tag in this repository, and publishes a GitHub Release with generated
release notes. Each release contains a multi-architecture
`OpenClawGateway-<version>.msixbundle` as the recommended Store submission, plus
`OpenClawGateway-<version>-x64.msix` and
`OpenClawGateway-<version>-arm64.msix` packages for architecture-specific
deployment. Store-mode assets are intentionally unsigned and are not direct
sideload downloads; Partner Center signs them during ingestion. The duplicate
GitHub Actions artifacts remain short-lived transport and diagnostic copies.

The same identity can be used for direct distribution and Microsoft Store
submission; the fourth component is always `0`.

The signed `v0.0.0.0` and `v0.0.0.1` proof releases and the latest production
release retain the former `OpenClaw.Gateway` identity and remain immutable
transition baselines. Partner Center requires the reserved
`OpenClawFoundation.OpenClawGateway` identity, so Windows cannot update those
packages in place or retain their packaged LocalState. Pull requests that
change release policy download the hash-pinned standalone x64 and recommended
`.msixbundle` assets, install each one on a clean GitHub-hosted Windows runner,
verify the legacy identity, remove it, install the candidate through the same
delivery format, and prove that Windows registered the reserved package family
with isolated LocalState. Changes to source-selection scripts also trigger this
check against the selected release. The gate additionally proves fresh
installation of both candidate formats. It refuses to run when a Gateway package is
already registered and removes only packages installed by that test
invocation. It temporarily trusts the ephemeral test-signing certificate in
the local-machine Trusted People store, as required by Windows deployment, and
removes that certificate in `finally`. The resulting JSON evidence is retained
as a workflow artifact for 90 days. This identity change is the explicitly
approved breaking reset; future releases under the reserved identity must
return to in-place upgrade and LocalState-retention proof.

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
is recorded in `release-policy.json`. Before an official dispatch, the
certificate profile must issue that exact Partner Center publisher subject;
signature verification fails closed when it does not.

## Installed data

| Data | Default path |
|---|---|
| OpenClaw application files | Read-only MSIX package `app` directory |
| Bundled Node.js archive | Read-only MSIX package `runtime` directory |
| Extracted Node.js runtime | `%LOCALAPPDATA%\Packages\<package-family>\LocalState\OpenClaw\NodeJS\node-v<version>-win-<architecture>` |
| Staged native dependency packages | `%LOCALAPPDATA%\OpenClawGatewayMSIX\agent-native\<content-id>` (agent account) |
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

Native dependency staging does not move that boundary. The copies live in the
agent's own profile, which already holds the extracted Node.js runtime and is
written and read by the same identity that executes it; nothing is staged into
the guest-writable shared workspace. Application code continues to execute from
the immutable package, and redirection is gated on a staged file existing, so a
package that was not staged resolves exactly as before.

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
