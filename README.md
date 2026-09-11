# OpenClaw Windows MSIX

This repository builds a Windows MSIX package containing:

- one .NET 10 NativeAOT launcher exposed through the `openclaw` and `clawctl`
  app execution aliases;
- a pinned, verified build of
  [`openclaw/openclaw`](https://github.com/openclaw/openclaw).

Node.js is a device prerequisite and is never downloaded or included in the
MSIX.

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

Before launching, the host discovers `node.exe` on `PATH` and verifies its
version and executable architecture. It never downloads, installs, or services
Node.js.

The expanded OpenClaw application is installed read-only inside the MSIX.
After resolving Node.js, the launcher confirms that packaged
`app\openclaw.mjs` exists and executes it directly. It does not extract, hash,
copy, repair, or otherwise change package files at runtime.

Every OpenClaw child process runs with
`OPENCLAW_SUPERVISOR_MODE=external`,
`OPENCLAW_SERVICE_REPAIR_POLICY=external`, and
`OPENCLAW_NO_AUTO_UPDATE=1`. These declare external lifecycle ownership,
prevent doctor-owned service repair, and disable configured background
auto-updates. The pinned OpenClaw `v2026.8.2` release honors external supervisor
mode by refusing native service mutation and OpenClaw self-update with guidance
to use the external supervisor's workflow. This behavior belongs to upstream
OpenClaw; the launcher does not reserve, reject, or rewrite upstream command
arguments.
OpenClaw inherits the terminal's working directory; the launcher does not make
the read-only application directory the workspace.

### `clawctl`

`clawctl` exposes package readiness and launcher version information:

| Command | Behavior |
|---|---|
| `clawctl setup` | Verify compatible Node.js is on `PATH` and confirm packaged `app\openclaw.mjs` exists. |
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

`setup` requires a compatible device-installed Node.js runtime. Missing,
outdated, malformed, or architecture-incompatible runtimes produce an
actionable error rather than a later process-launch failure.

`clawctl setup` is read-only. It performs no extraction, hashing, inventory
walk, or state mutation.

The launcher places Node.js in a Windows job configured with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. The launcher remains alive while Node.js
runs; if the launcher exits or is terminated, Windows terminates Node.js and
its child processes when the job handle closes.

Install the current Node.js LTS release, open a new terminal, optionally check
readiness, then use `openclaw`:

```powershell
winget install --id OpenJS.NodeJS.LTS --exact --source winget
clawctl setup
openclaw
```

The packaged OpenClaw revision accepts Node.js
`>=22.22.3 <23 || >=24.15.0 <25 || >=25.9.0`. The launcher keeps this
requirement in one shared validator used by `clawctl` and `openclaw`.

## Selecting the OpenClaw revision

`.github\workflows\gateway-msix.yml` resolves an explicit OpenClaw ref before
building. Pull-request and `main` push runs use the pinned commit configured in
both:

- `workflow_dispatch.inputs.openclaw_ref.default`;
- the non-manual fallback in `env.OPENCLAW_REF`.

Changing only the workflow-dispatch default does not change automatic builds.
For a one-time override, run **Build OpenClaw Gateway MSIX** manually and
provide a tag, branch, or preferably a full 40-character commit SHA in
`openclaw_ref`.

The payload artifact records the requested ref and resolved upstream commit in
`payload-metadata.json`. That build-only file is not embedded in the MSIX.
`msix-metadata.json` records both the packaging repository commit and bundled
OpenClaw commit, while embedded `payload-files.json` records every packaged
application file's path, length, and SHA-256.

`release-policy.json` records the immutable OpenClaw commit approved for
official signing. Updating that policy requires a reviewed repository change.
Official signing runs only from `main` and verifies the workflow input, both
architecture metadata files, both MSIX hashes, the embedded manifests, and
every file against the embedded application inventory before requesting Azure
credentials.

## Build and test

```powershell
dotnet restore .\OpenClaw.Gateway.MSIX.slnx
dotnet test .\OpenClaw.Gateway.MSIX.slnx `
  --configuration Release `
  --no-restore
```

`scripts\Build-Payload.ps1` npm-installs an OpenClaw package into an expanded,
architecture-specific application tree. `scripts\Build-MSIX.ps1` copies that
tree into package content, rejects any Node.js executable or runtime archive,
creates a per-file inventory, and then creates an unsigned NativeAOT MSIX.
`scripts\Build-LocalMSIX.ps1` can reuse a successful workflow payload or a
local payload directory. The Node.js used by the payload build jobs is build
infrastructure only and is not copied into the MSIX.

Normal pull-request and push workflows publish unsigned packages for
validation. Manual runs support three signing modes:

- `unsigned` accepts any OpenClaw branch, tag, or commit and publishes unsigned
  MSIX packages;
- `test` accepts any OpenClaw ref and publishes MSIX packages signed with a
  temporary self-signed certificate plus the public `.cer` needed for local
  installation;
- `official` requires the approved immutable commit from
  `release-policy.json` and may run only from `main`.

Official signing uses the protected `release-signing` environment, Azure OIDC,
and the existing OpenClaw Artifact Signing account and certificate profile.
Test-signing private keys are generated only on the temporary GitHub runner
and are deleted before artifacts are uploaded. No signing secret or private
key is stored in the repository.

## Installed data

| Data | Default path |
|---|---|
| OpenClaw application files | Read-only MSIX package `app` directory |
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
`Build-MSIX.ps1` rejects bundled Node.js, copies the tree into package content,
and records every application file's path, length, and SHA-256 in
`payload-files.json`. Package construction verifies that exact inventory
against the generated MSIX. Official signing authorization repeats the
inventory validation, including rejecting missing, changed, duplicate, unsafe,
or unlisted application entries, before requesting signing credentials.

At runtime, Windows' MSIX package integrity and read-only enforcement is the
trust boundary. `openclaw` and `clawctl setup` only check that
`app\openclaw.mjs` exists; neither performs file hashing or an inventory walk.
This avoids redundant startup overhead while keeping package mutation under
Windows servicing control.

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
