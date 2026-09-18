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
  --filter "FullyQualifiedName=OpenClaw.Launcher.Tests.HostOptionsTests.ParseForwardsAllArgumentsUnchanged"

# Exercise the official-signing policy checks.
.\scripts\Test-SigningInputs.Tests.ps1
```

To compose an unsigned local MSIX from the latest successful `main` workflow
payload, use:

```powershell
.\scripts\Build-LocalMSIX.ps1 -Architecture x64
```

Pass `-PayloadDirectory <path>` to use an already-built payload instead of
downloading one with `gh`. MSIX composition requires Visual Studio Build Tools
with the Desktop development with C++ workload and the Windows SDK. Build x64
and ARM64 separately.

For the development inner loop, register a Developer Mode layout instead of
building an MSIX:

```powershell
.\scripts\Deploy-LocalPackage.ps1
```

`Deploy-LocalPackage.ps1` publishes the NativeAOT launcher, assembles a layout
under `artifacts\local-package`, registers it with `Add-AppxPackage -Register`,
and runs `clawctl setup`. It never builds, signs, or installs an MSIX, and it
requires no changes to the packaging scripts or project files. It is idempotent
and skips work when nothing changed, so keep its up-to-date check honest: the
fingerprint hashes the launcher rather than trusting timestamps, because
publish can refresh timestamps with no source change.

Loose registration and MSIX installation are mutually exclusive for one package
identity, and Windows cannot preserve packaged app data across that switch, so
the script refuses by default and requires `-ReplaceExistingInstall`. The
registered package reads its files from the repository, so treat
`artifacts\local-package` and the checkout as live inputs, not scratch output.
Its scenario tests inject every GitHub, publish, certificate-free registration,
and deployment operation; no test may register, remove, or modify a real
package.

## Architecture

- `OpenClaw.Launcher` is a .NET 10 NativeAOT executable packaged as
  `openclaw.exe`. `Package.appxmanifest` declares the `OpenClaw.Gateway` MSIX
  identity and exposes the same binary through two app execution aliases,
  `openclaw.exe` and `clawctl.exe`. `HostEntrypointResolver` picks the surface
  from the package-qualified application user model ID, falling back to the
  invoked name parsed from the native command line; an unrecognized name falls
  back to the agent surface.
- `OpenClaw.SessionHost` is a second NativeAOT executable
  (`openclaw-session-host.exe`) that runs *inside* the isolated session under
  the agent identity. `OpenClaw.SessionProtocol` is the AOT-safe,
  source-generated JSON file contract the two processes exchange; it rejects
  unsupported schema versions.
- Sessions are mandatory. `openclaw` does not execute the packaged application
  directly. It requires the `clawctl setup` marker (`SetupStateStore`), starts
  or reuses the recorded isolated session through `SessionCoordinator`, and
  runs the work through `SessionExecutor`. Without a usable setup record it
  reports an error and exits nonzero rather than provisioning implicitly.
- Isolation is provided by the MXC runtime, not by this code.
  `MxcRuntimeLocator` resolves the architecture-specific `wxc-exec.exe` from
  package content and `MxcCliSessionClient` exchanges versioned JSON envelopes
  with it. `mxc-runtime.lock.json` pins the archive, integrity data, and the
  allowlisted runtime files; `scripts\Get-MxcRuntime.ps1` verifies every one of
  them and never runs package lifecycle scripts.
- `clawctl setup` stages the packaged session helper into a shared workspace
  through `SessionHelperStager`. The helper cannot execute in place from
  another package identity's `WindowsApps` directory, so the copy is required,
  not an optimization.
- The packaged application tree stays expanded and read-only, and
  `app\openclaw.mjs` remains the entry point, but it is executed inside the
  session. Node.js is installed by `SessionRuntimeInstaller` into the **agent
  account's profile** (`OpenClawGatewayMSIX\agent-node\<archive-root>`), not
  package LocalState, because the agent identity cannot read the launcher's
  LocalState. The session host prepends that runtime to the agent's `PATH`.
- `clawctl` parses its own arguments with System.CommandLine
  (`ClawCtlCommandLine` builds the tree; `Program.RunControlAsync` invokes it).
  The tree is `setup` (`--fresh`, `--force`), `status`, `collect-logs`
  (`--output`), `teardown` (`--force`), `pwsh` (which rejects `--json`), and
  `gateway-service` (`start` with a hidden `--recovery`, `status`, `stop`),
  plus the recursive global options `--json` and `--no-color`. Help, usage,
  version, and completion are library behavior; parse errors exit `1`.
  Response-file expansion is disabled, so `@file` is an ordinary unrecognized
  argument. The library is scoped to `clawctl` only and must never see
  `openclaw` arguments.
- The gateway is not a foreground child. `SchTasksGatewayScheduler` persists it
  as a Windows logon task through inbox `schtasks.exe`, and
  `GatewayConfigurationStore` persists the launch configuration because the
  task starts later with no interactive caller. Do not force a port: an absent
  configured port is omitted so OpenClaw resolves its own `gateway.port`. The
  recorded `18789` is upstream's default, kept for guidance only, so never
  report it as an observed address. `GatewayController` reports status from the
  observed listening port.
- Diagnostics are written to packaged LocalState (or
  `%LOCALAPPDATA%\OpenClawGatewayMSIX` outside an MSIX context) with a named
  mutex so concurrent processes append complete records. `collect-logs` emits a
  timestamped ZIP, excludes credential databases and auth profiles, and passes
  included text through `DiagnosticsRedactor`.
- The GitHub workflow first builds and packs a pinned `openclaw/openclaw`
  revision on Linux using that revision's `setup-node-env` action. Non-official
  runs cache that tarball by resolved upstream commit and verify its recorded
  commit and SHA-256 on every use. They also cache each architecture's Windows
  dependency tree by commit, Node.js version, and payload script hash while
  rerunning all payload validation; official signing bypasses both caches. The
  resolved Node.js version flows through `source.json` and `payload-metadata.json`;
  each architecture-specific Windows job uses that same version to build the
  expanded payload, validates the installed Gateway and Control UI build identities,
  and immediately composes its MSIX. `Build-MSIX.ps1` downloads the matching
  official archive, rejects Node.js from the application payload, builds the
  application inventory, publishes the NativeAOT host, validates package contents,
  and emits MSIX metadata including the runtime hash.
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
- Preserve execution of the packaged `app\openclaw.mjs` from the immutable
  package and the caller's working directory, but run it through the isolated
  session; never copy the OpenClaw application payload. Node.js installation
  belongs to `SessionRuntimeInstaller` and targets the agent account's profile,
  not package LocalState, because the agent identity cannot read the launcher's
  LocalState.
- Sessions are mandatory and explicit. `clawctl setup` owns provisioning and
  writes the setup marker; `openclaw` starts only the recorded session and
  never provisions implicitly. Do not add an implicit-provisioning fallback.
- Publishing the session host is a separate NativeAOT step. Restore
  `src\OpenClaw.SessionHost\OpenClaw.SessionHost.csproj` with the target runtime
  and `PublishAot=true` before `Build-MSIX.ps1` publishes it with
  `--no-restore`; the ordinary solution restore is not sufficient.
- The MXC runtime is a release trust input. Keep `mxc-runtime.lock.json`, the
  allowlisted file set, and the per-file integrity checks in
  `scripts\Get-MxcRuntime.ps1` synchronized, and keep the staged runtime and
  session host in the build inventory for both architectures.
- The build-time inventory is a release trust boundary. Keep safe unique paths,
  lengths, and SHA-256 values synchronized across composition and signing
  validation.
- Keep x64 and ARM64 behavior synchronized across the workflow matrix, scripts,
  project runtime identifiers, manifest content, payload metadata, and signing
  validation.
- Do not add a packaging-side Node.js version pin or support-range policy.
  The selected upstream toolchain owns version selection; package composition
  supplies `NodeRuntimeArchiveFileName`, and the host reads the archive name.
- Official releases combine the x64 and ARM64 packages into one signed
  `.msixbundle` while retaining signed standalone packages for explicit
  architecture-specific deployment. Compose the bundle before signing; bundle
  signing recursively covers its contained packages.
- Metadata files are part of the release trust chain, not incidental build
  output. Changes to their fields must be coordinated across payload creation,
  MSIX creation, signing validation, workflow artifacts, and tests.
- Keep the workflow's manual `openclaw_ref` default and automatic
  `env.OPENCLAW_REF` fallback identical. Official-release changes also update
  the reviewed immutable commit and stable or correction tag in
  `release-policy.json`. The tag determines the four-part MSIX identity
  version and the permanent GitHub Release tag.
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

## Documentation

Three project skills in `.github\skills\` cover documentation and pre-review
cleanup: `technical-documentation` for authoring and reviewing,
`docs-refactor` for whole-page rewrites that must not lose behavior facts, and
`deslop` for behavior-neutral cleanup of a branch diff before review.

`.\scripts\Test-DocReferences.ps1` is an advisory checker for documentation
references: it validates that repository paths named in markdown are tracked,
that relative links and anchors resolve, and that markdown stays LF. It
resolves paths through `git ls-files` rather than the filesystem, because
stale untracked build output would otherwise make a renamed path look valid.
CI runs it with `-Advisory`, which annotates without failing the build; run it
without that switch locally to get a nonzero exit on findings.

Keep `README.md`, `CONTRIBUTING.md`, and this file consistent with each other
and with source. When they disagree, source wins.
