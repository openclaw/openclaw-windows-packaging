# OpenClaw Gateway MSIX repository guidance

## Build and test commands

The repository is Windows-focused and pins the .NET 10 SDK through `global.json`.
Run commands from the repository root in PowerShell 7 (`pwsh`).

```powershell
# Restore and build the launcher and tests without packaging content.
dotnet restore .\OpenClaw.Gateway.MSIX.slnx
dotnet build .\OpenClaw.Gateway.MSIX.slnx --configuration Release --no-restore

# Canonical quality gate: restore plus a Release rebuild with static analysis.
# Used by local development, CI, and the optional pre-push hook.
.\scripts\Test-DotNetQuality.ps1

# Publish the launcher through the NativeAOT toolchain without MSIX content.
$vsInstaller = Join-Path `
  ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) `
  'Microsoft Visual Studio\Installer'
$env:Path = "$vsInstaller;$env:Path"
dotnet publish .\src\OpenClaw.Launcher\OpenClaw.Launcher.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained

# Run all .NET tests.
dotnet test .\OpenClaw.Gateway.MSIX.slnx `
  --configuration Release `
  --no-restore

# Run one xUnit test by fully qualified name.
dotnet test .\tests\OpenClaw.Launcher.Tests\OpenClaw.Launcher.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName=OpenClaw.Launcher.Tests.ProgramTests.AgentLaunchResolvesNodeAndRunsPackagedApplication"

# Stage the pinned MXC native runtime (packaging content only).
.\scripts\Get-MxcRuntime.ps1 -Architecture x64

# Exercise the official-signing policy checks.
.\scripts\Test-SigningInputs.Tests.ps1
```

To compose and test-sign a local MSIX from the latest successful `main`
workflow payload, use:

```powershell
.\scripts\Build-LocalMSIX.ps1 -Architecture x64
```

Pass `-PayloadDirectory <path>` to use an already-built payload instead of
downloading one with `gh`. MSIX composition requires Visual Studio Build Tools
with the Desktop development with C++ workload and the Windows SDK. Build x64
and ARM64 separately.

Install the newest test-signed local package with:

```powershell
.\scripts\Install-LocalMSIX.ps1 -Architecture x64
```

## Architecture

- `OpenClaw.Launcher` (`src\OpenClaw.Launcher`) is a .NET 10 NativeAOT
  executable packaged as `openclaw.exe`. `Package.appxmanifest` exposes it
  through the `openclaw.exe` app execution alias and declares the
  `OpenClaw.Gateway` MSIX identity.
- The package contains an expanded, read-only OpenClaw application tree.
  `HostOptions` resolves `app\openclaw.mjs` directly from the package.
- `openclaw` resolves device-installed Node.js, confirms the packaged entry
  point exists, and forwards every argument unchanged to `openclaw.mjs`.
- `clawctl` parses its own arguments with System.CommandLine
  (`ClawCtlCommandLine` builds the tree; `Program.RunControlAsync` invokes it).
  Help, usage, version, and completion are library behavior; parse errors exit
  `1`. Response-file expansion is disabled, so `@file` is an ordinary
  unrecognized argument. The library is scoped to `clawctl` only and must never
  see `openclaw` arguments. Operations reach the tree through `ClawCtlHandlers`
  so parsing stays in the tree and the operations stay substitutable; a bare
  noun prints its own help rather than defaulting to a sub-command.
- `clawctl setup` is the explicit lifecycle entry point. It may write
  package-scoped LocalState for the owned session, gateway configuration,
  sign-in recovery, and its setup marker, but runtime launches and setup do not
  hash or walk package files.
- `src\OpenClaw.Launcher\Mxc` holds project-owned MXC lifecycle contracts
  (`IMxcSessionClient`) and a temporary command-line transport over the pinned
  `@microsoft/mxc-sdk` binaries. Preview wire details stay inside
  `MxcWireProtocol` and `MxcCliSessionClient` so the official .NET SDK can
  replace the transport behind the same contract. See `docs\mxc-runtime.md`.
- `src\OpenClaw.SessionHost` is a separate NativeAOT guest helper that runs
  inside the isolated session, and `src\OpenClaw.SessionProtocol` holds the
  launch request/result contract both executables share. The backend's
  execution API flattens its command line through `cmd.exe`, so the argument
  vector always travels as data and is never interpolated into that command
  line. See `docs\session-host.md`.
- `HostPaths` derives every writable path from one state root, and
  `PackageIdentity` supplies the package family name and the `PFN:` MXC
  application id. `SessionStateStore` records the owned session; ownership is
  never inferred from machine state, because `deprovision` is idempotent and
  unrelated agent accounts can already exist. See `docs\session-state.md`.
- `SessionCoordinator` owns the session lifecycle, `SessionExecutor` runs a
  single OpenClaw invocation inside it through the guest helper, and
  `SessionRuntime` composes both from the running installation. The backend
  client is constructed lazily so `clawctl session status` stays readable on a
  machine without the MXC runtime.
- `SessionRoutingPolicy` decides between session and host execution.
  Sessions are default-on wherever the backend probe reports support; an
  unsupported machine runs directly, and a *required* session that cannot be
  provided fails loudly rather than falling back to the host. See
  `docs\session-routing.md`.
- Gateway management lives under `clawctl gateway-service`, never `gateway`,
  because OpenClaw itself owns `openclaw gateway run`. The gateway runs
  detached inside the session; ownership is proven by a live process, a
  matching creation time, and a listener resolved to that process or a
  descendant, never by a recorded identifier alone. See
  `docs\gateway-service.md`.
- `GatewayLauncher` starts Node without a shell, uses `ArgumentList`, inherits
  the console streams, and sets `OPENCLAW_SUPERVISOR_MODE=external` plus
  `OPENCLAW_NO_AUTO_UPDATE=1`. The child process exit code is the launcher exit
  code.
- Diagnostics are written to packaged LocalState (or
  `%LOCALAPPDATA%\OpenClawGatewayMSIX` outside an MSIX context) with a named
  mutex so concurrent processes append complete records.
- The GitHub workflow first builds and packs a pinned
  `openclaw/openclaw` revision on Linux. Windows matrix jobs use
  `Build-Payload.ps1` to produce x64/ARM64 expanded trees and build metadata,
  then `Build-MSIX.ps1` to reject bundled Node.js, build the application
  inventory, publish the NativeAOT host, validate package contents, and emit
  MSIX metadata.
- Unsigned artifacts are the normal PR/push output. Test signing uses a
  temporary runner-local certificate. Official signing is gated to `main` and
  the immutable upstream commit in `release-policy.json`; signing inputs are
  validated before Azure credentials are requested.

## Repository conventions

- Static analysis uses only the analyzers shipped by the pinned SDK.
  `Directory.Build.props` sets `AnalysisMode=All`, `EnforceCodeStyleInBuild`,
  and `GenerateDocumentationFile` (required for build-time `IDE0005`), and
  suppresses `CS1591`. Rule severity belongs in the root `.editorconfig`, not
  in the project files. `TreatWarningsAsErrors` is on and the build is
  warning-free, so any new warning fails the build; only NuGet audit
  advisories (`NU1901`-`NU1904`) are excluded, because a new advisory can break
  an unchanged dependency graph. Do not commit a generated suppression
  baseline; fix the diagnostic or add a narrow suppression with a written
  rationale.
- The pre-push hook is opt in. `scripts\Install-GitHooks.ps1` copies the
  tracked `hooks\pre-push` into the current clone and `-Remove` deletes it.
  Never change `core.hooksPath` or global Git configuration, and never
  overwrite a hook the repository did not write.
- `.gitattributes` normalizes tracked text to LF and `.editorconfig` sets
  `end_of_line = lf`. Avoid whole-file rewrites through `Set-Content` or
  `Out-File`, which reintroduce CRLF.
- Ordinary builds and tests must leave `IncludePackagingContent` unset.
  Packaging builds set it to `true`, supply a runtime identifier and platform,
  and use `obj\packaging` through `Directory.Build.props` to isolate MSIX
  intermediates.
- Treat launcher arguments as OpenClaw-owned. Do not add host-only switches,
  consume `--`, rewrite arguments, or block upstream commands; tests explicitly
  protect transparent forwarding.
- Preserve direct application execution from the immutable package and the
  caller's working directory. The one required copy is the NativeAOT session
  helper: `clawctl setup` stages it into the shared workspace because the agent
  identity cannot execute another package's WindowsApps binary. Do not extract
  or copy the application tree, hash it, or walk its inventory at runtime.
- The build-time inventory is a release trust boundary. Keep safe unique paths,
  lengths, and SHA-256 values synchronized across composition and signing
  validation.
- `mxc-runtime.lock.json` is a reviewed release trust-chain input like
  `release-policy.json`. Changing the pinned package, version, integrity value,
  or per-file hashes requires updating acquisition, MSIX composition,
  `msix-metadata.json` fields, signing validation, and the signing tests
  together.
- Keep x64 and ARM64 behavior synchronized across the workflow matrix, scripts,
  project runtime identifiers, manifest content, payload metadata, and signing
  validation.
- Metadata files are part of the release trust chain, not incidental build
  output. Changes to their fields must be coordinated across payload creation,
  MSIX creation, signing validation, workflow artifacts, and tests.
- Keep the workflow's manual `openclaw_ref` default and automatic
  `env.OPENCLAW_REF` fallback identical. Official-release changes also update
  the reviewed immutable commit in `release-policy.json`.
- The launcher is NativeAOT. `dotnet build` and the xUnit suite exercise a JIT
  build, so run the NativeAOT publish path when changing reflection, interop,
  or trimming-sensitive code.
- Package versions have four numeric components that each fit in `UInt16`.
  Package dependency versions belong in `Directory.Packages.props`.
- PowerShell build scripts fail fast with `$ErrorActionPreference = 'Stop'`
  and must also check `$LASTEXITCODE` after native tools. Preserve metadata and
  hash validation rather than relying only on command success.
- Tests create isolated temporary directories through `TestDirectory`; extend
  those fixtures instead of reading or modifying real OpenClaw profile,
  packaged LocalState, or installed MSIX data.
