# Pinned MXC runtime

The package will run the OpenClaw Gateway inside a Windows isolated agent
session rather than the interactive logon session. That capability is provided
by MXC. This document describes how the MXC runtime enters the build, why it is
pinned, and how the dependency changes once the official MXC .NET SDK is
published.

Session provisioning and execution are not implemented yet. The current
launcher only *detects* whether the prerequisites for isolated sessions are
present; see [Readiness reporting](#readiness-reporting).

## Why a pinned native runtime

The MXC .NET SDK is not published. Until it is, the only public, verifiable
distribution of the MXC backend executables is the
[`@microsoft/mxc-sdk`](https://www.npmjs.com/package/@microsoft/mxc-sdk) npm
package, which ships architecture-specific `wxc-exec.exe` and `plm.exe`
binaries signed by Microsoft.

Those executables are used through their documented command-line protocol. The
npm package's TypeScript SDK, Node.js, node-pty, unrelated backends, and test
proxies are deliberately not used and never enter the MSIX.

## `mxc-runtime.lock.json`

The repository-root lock file is the pin. It records:

| Field | Purpose |
|---|---|
| `package`, `version` | The exact npm package identity. |
| `tarballUrl`, `tarballIntegrity` | The registry archive and its `sha512` subresource integrity value. |
| `wireSchemaVersion` | The IsolationSession envelope schema version, which is versioned separately from the npm package version. |
| `minimumWindowsBuild` | The lowest Windows build whose OS IsolationSession backend the pinned runtime supports. |
| `architectures.<arch>.files` | Each staged runtime file's archive path, staged path, length, SHA-256, and expected PE machine type. |
| `licenseFiles` | Licensing material that accompanies the redistributed binaries. |

Changing any of these values is a reviewed repository change, exactly like
`release-policy.json`. The signing-policy validator reads this file and rejects
any MSIX whose staged MXC files do not match it byte for byte.

## Acquisition

`scripts\Get-MxcRuntime.ps1` stages the runtime. It never runs `npm install`,
never runs npm lifecycle scripts, and never executes a downloaded program.

```powershell
# Download (or reuse the cache) and stage the x64 runtime.
.\scripts\Get-MxcRuntime.ps1 -Architecture x64

# Stage from an already-downloaded archive, for offline iteration.
.\scripts\Get-MxcRuntime.ps1 -Architecture arm64 -ArchivePath .\mxc-sdk-0.8.0.tgz
```

For each run the script:

1. resolves the archive from the gitignored cache, an explicit `-ArchivePath`,
   or the pinned registry URL;
2. verifies the archive's `sha512` against `tarballIntegrity` before reading
   any entry;
3. extracts only the allowlisted entries for the requested architecture;
4. verifies each extracted file's length, SHA-256, and PE machine type;
5. atomically replaces `content\mxc\<arch>\` from an isolated staging
   directory; and
6. writes `content\mxc\<arch>\mxc-runtime.json` provenance recording the
   package, version, architecture, and archive integrity value.

`-ArchivePath` is an offline convenience, not an escape hatch: a local archive
is subject to the same integrity, allowlist, and architecture checks.

Staging is idempotent. A directory that already matches the lock is left alone;
a tampered or partial directory is restaged. `-Force` restages unconditionally.

`content\mxc\` is gitignored. The staged runtime is build output, not source.

## Build integration

`scripts\Build-MSIX.ps1` invokes the acquisition script for the architecture it
is composing, validates the staged provenance, adds every `mxc/<arch>/...`
entry to the expected package file set, and records `mxcRuntimePackage`,
`mxcRuntimeVersion`, `mxcRuntimeIntegrity`, and `mxcRuntimeFileCount` in
`msix-metadata.json`. `scripts\Build-LocalMSIX.ps1` delegates to it, so local
composition and CI stage the same pinned files.

`scripts\Test-SigningInputs.ps1` treats the runtime as part of the release
trust chain. Before Azure credentials are requested it verifies the lock file,
the four metadata fields, the exact in-package MXC file set, every pinned hash,
and the embedded provenance record. `scripts\Test-SigningInputs.Tests.ps1`
covers substituted, added, and removed runtime files plus provenance and
metadata drift.

Ordinary `dotnet build` and `dotnet test` do **not** require the runtime. It is
packaging content, gated behind `IncludePackagingContent`.

## Readiness reporting

`clawctl setup` remains read-only. It now also reports whether isolated
sessions are usable, without provisioning anything:

- whether a pinned runtime is present next to the launcher for the current
  process architecture, and which package version it came from;
- whether the OS IsolationSession backend is actually usable here.

Backend support is **measured**, not inferred. When the runtime is present,
`setup` runs the executor's own non-mutating host capability detector
(`wxc-exec --probe`), which creates no sandbox, and reports its verdict plus the
backend tier and any warnings it raises. A build number only predicts support;
the detector is the one signal that notices a host where the feature is present
but unusable.

If the runtime is missing, or the detector cannot run, `setup` falls back to
comparing the current Windows build against `minimumWindowsBuild`, says so in
the output, and reports the probe failure reason when there is one.

An unavailable runtime or an unsupported host is reported plainly and does not
fail `setup`, because Node.js and the packaged entry point can still be correct
for today's non-isolated execution. An undeterminable Windows build is reported
as unknown rather than unsupported.

Set `OPENCLAW_MXC_RUNTIME_DIR` to point the locator at a runtime directory
outside the package. This exists for development and diagnosis; the locator
never searches `PATH`.

## Transition to the MXC .NET SDK

Everything above is a temporary transport. The project-owned contract is
`IMxcSessionClient` in `src\OpenClaw.Launcher\Mxc\MxcSessionContracts.cs`,
shaped after the upcoming SDK's state-aware lifecycle
(provision, start, exec, stop, deprovision). Preview command-line and JSON
envelope details are confined to `MxcWireProtocol` and `MxcCliSessionClient`.

When the SDK is published, an `MxcSdkSessionClient` satisfies the same
behavioral contract, the shared adapter tests run against both, and the npm
lock, acquisition script, packaging content, and signing checks for the native
binaries are removed. Session and gateway behavior must not change merely
because dependency delivery changed.

The implementation stack stays draft until that replacement lands.
