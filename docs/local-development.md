# Local development

This contributor how-to covers the Windows package inner loop: registering a
Developer Mode layout, composing an unsigned MSIX, test-signing that artifact,
and checking the result. It does not own the product command contract or the
general build/test policy; start with [running a local development
build](../README.md#running-a-local-development-build) and [build, analyze, and
test](../CONTRIBUTING.md#build-analyze-and-test).

Run commands from the repository root in PowerShell 7 (`pwsh`). You need the
pinned .NET SDK, Developer Mode for loose registration, and Visual Studio Build
Tools with the Desktop development with C++ workload plus the Windows SDK for
NativeAOT publishing and MSIX work.

## Choose the loop

| Need | Authority and outcome |
| --- | --- |
| Iterate on the packaged application without producing an MSIX | `.\scripts\Deploy-LocalPackage.ps1 -Patch <name>` publishes and registers a Developer Mode layout under a side-by-side identity named for the work. This is the normal inner loop. |
| Exercise the base `openclaw` and `clawctl` identity itself | `.\scripts\Deploy-LocalPackage.ps1` without `-Patch` registers `OpenClawFoundation.OpenClawGateway`, for alias, shell completion, or loose/MSIX transition work. |
| Produce an unsigned artifact | `.\scripts\Build-LocalMSIX.ps1` composes an unsigned MSIX from a payload; it does not install it. |
| Make a local artifact installable for testing | `.\scripts\Sign-TestMSIX.ps1` signs the output separately with a temporary test certificate. |
| Install a signed local artifact | After removing a base loose registration with the deployment script, install the signed package with `Add-AppxPackage -Path <signed-msix>`. |

Loose registration and an installed MSIX are mutually exclusive for the
`OpenClawFoundation.OpenClawGateway` identity. Windows cannot preserve packaged app data while
switching between them. Treat a transition as deliberate, not an update.

Checkouts that previously registered the legacy `OpenClaw.Gateway` identity
need an explicit transition because the old registration may still point at
this checkout's layout. The deployment script detects both identities before
changing that layout. Run `-Unregister` to remove an owned loose registration,
or pass `-ReplaceExistingInstall` to explicitly remove the old registration as
part of deployment; packaged LocalState does not transfer to the reserved
package family.

## Fast, loose-registration inner loop

From a clean checkout, register a layout for this device's architecture under a
patched identity named for the change you are iterating on, and prepare its
runtime:

```powershell
.\scripts\Deploy-LocalPackage.ps1 -Patch pwsh-exec
```

`-Architecture` defaults to the device's native architecture; pass the same
value to `-Unregister`. An x64 layout registers on an ARM64 device, but its
`clawctl setup` fails there because the x64 MXC executor exits with
`0xC000007B`.

The script resolves a payload, stages MXC and the bundled Node.js runtime,
publishes both the NativeAOT `openclaw.exe` launcher and the NativeAOT session
host, assembles the layout, registers it with `Add-AppxPackage -Register`, and
runs setup through that identity's control application. The patch registers
beside the base package as `openclaw-pwsh-exec` and `clawctl-pwsh-exec`, so
iteration never replaces the base `openclaw` and `clawctl` or their app data.
Its layout lives under
`artifacts\local-package\patches\pwsh-exec\<architecture>\layout`. A
descriptive name keeps concurrent iterations apart and makes the registration
recognizable in `Get-AppxPackage` output.

Omit `-Patch` only when the work exercises the base identity itself: its
`openclaw` and `clawctl` aliases, shell completion, or a loose/MSIX transition.
That layout lives under `artifacts\local-package\<architecture>\layout`.

**When you finish, tear down and unregister the patch.** Otherwise its
isolated session, any gateway logon task, and its registration stay behind;
see [remove a patch](#remove-a-patch).

Use options for a concrete reason:

| Option | Use it when |
| --- | --- |
| `-Architecture x64` or `arm64` | The target architecture is runnable on this device. Omitted, it selects the device's native architecture. |
| `-PayloadDirectory <path>` | You already have an expanded payload containing `app` and `payload-metadata.json`, or need an offline/reproducible iteration. It is read directly, is not modified, makes no GitHub request, and must be supplied on every run. |
| no payload option | You want the script to resolve the latest successful `main` payload workflow. |
| `-PayloadRunId <id>` | You need a specific successful workflow payload instead of the latest one. Use the current branch's successful workflow run when that branch changes packaged application content. |
| `-RefreshPayload` | A cached payload must be downloaded again; the previous payload remains available until the replacement registers successfully. |
| `-Force` | You intentionally need to re-register although the recorded inputs have not changed. |
| `-SkipSetup` | You only need the registration and intentionally want to defer setup. If setup state is absent, the next `openclaw` invocation performs setup automatically unless `CLAWCTL_AUTO_SETUP` suppresses it; run `clawctl setup` when you want to provision or recover explicitly. |
| `-ReplaceExistingInstall` | You explicitly accept removal of an installed MSIX or a registration owned by another checkout. This is destructive because app data cannot survive the loose/installed transition. |
| `-Patch <name>` | You are iterating on a change; this is the recommended default. Name it for the work so its commands, app data, and isolated session stay separate from the base package. See [side-by-side patched identities](#side-by-side-patched-identities). |

The command is idempotent, not timestamp-driven. `LocalPackage.psm1` records
two content-hash fingerprints:

- The build-input fingerprint covers the launcher, session-host, and protocol
  project files other than `bin` and `obj`, `Directory.Build.props`,
  `Directory.Packages.props`, `global.json`, the checkout commit, the payload
  identity, and the manifest, images, Node.js scripts, and staged MXC runtime.
  The package version is compiled into the launcher, so this fingerprint picks
  the version before publishing: a changed deployment publishes the NativeAOT
  launcher once, with the version it registers.
- The output fingerprint, which includes the SHA-256 hashes of the published
  launcher and session host, alone decides that the package is current. A
  publish can refresh timestamps without changing code, so timestamps would
  spuriously redeploy the layout. When the build inputs match but the binaries
  changed anyway, for example after an SDK update, the command rebuilds the
  launcher with a new version instead of reporting the package as current.

With both fingerprints unchanged, the command reports that the package is
current. Any change to a covered input redeploys with a new version, even an
edit, such as a comment, that leaves the binaries unchanged.

**Never delete `artifacts\local-package` or the checkout while it is
registered.** The Developer Mode package reads those live files, so either
action breaks it until redeployment. The expanded payload is linked rather than
copied, which is why repeat deploys avoid duplicating it.

### Verify the registered package

```powershell
Get-AppxPackage -Name OpenClawFoundation.OpenClawGateway-pwsh-exec |
  Select-Object PackageFullName, InstallLocation, IsDevelopmentMode, SignatureKind

clawctl-pwsh-exec --version
clawctl-pwsh-exec status
```

For the base identity, query `OpenClawFoundation.OpenClawGateway` and run
`clawctl` instead.

For a loose layout, `InstallLocation` should be the checkout's
`artifacts\local-package\patches\pwsh-exec\<architecture>\layout` (base:
`artifacts\local-package\<architecture>\layout`) and `IsDevelopmentMode` should
be `True`. `clawctl --version` prints the package version and packaging commit
alongside the payload version and commit; compare these to the payload and
checkout you intended to use. `clawctl status` is an active status probe: it
asks the backend to start the recorded provision, but does not create a
replacement provision or start the gateway.

After a successful loose deployment, expect the registration to report the
current checkout's layout directory, `IsDevelopmentMode` as `True`, and
`SignatureKind` as `None`. The packaging commit from `clawctl --version` must
match the checkout you deployed; the payload commit must match the payload
selected by the deployment script. A mismatch means the command is reaching
another registration or the wrong payload, even if the deployment command
itself succeeded.

### Side-by-side patched identities

`-Patch <name>` registers a separate development identity next to the base
package. It is a local inner-loop affordance: it lets you keep several
deployments registered at the same time for development and testing, such as
parallel branches or agent sessions, or a change beside an installed release.
It is not a product or release identity. Name it for the work, such as a
branch topic, so each iteration is easy to identify and remove:

```powershell
.\scripts\Deploy-LocalPackage.ps1 -Patch pwsh-exec
clawctl-pwsh-exec --version
clawctl-pwsh-exec status
```

| Base identity | With `-Patch pwsh-exec` |
| --- | --- |
| `OpenClawFoundation.OpenClawGateway` | `OpenClawFoundation.OpenClawGateway-pwsh-exec` |
| `openclaw`, `clawctl` | `openclaw-pwsh-exec`, `clawctl-pwsh-exec` |
| Display name `OpenClaw Gateway` | `OpenClaw Gateway (pwsh-exec)` |
| `artifacts\local-package\<architecture>` | `artifacts\local-package\patches\pwsh-exec\<architecture>` |

The name is lowercased and must be 1 to 15 letters, digits, or hyphens,
starting and ending with a letter or digit. A patched identity has its own
package family, so it also has its own LocalState, setup state, isolated
session, and gateway logon task. The script neither checks nor changes the
base or legacy registrations for a patched deployment, so the patch can sit
beside an installed MSIX or this checkout's base loose registration. Each
patch keeps its own payload cache, so its first deployment downloads the
payload again unless you pass `-PayloadDirectory`.

Known limits. These are accepted because patched identities exist only for
local concurrent development and testing:

- Help, errors, and next-step guidance, including setup and teardown recovery
  instructions, still print `clawctl` and `openclaw`. Always substitute the
  patched command, such as `clawctl-pwsh-exec setup`. Running the printed base
  command verbatim acts on the base package and its isolated session when one
  is installed, and leaves the patch unrecovered.
- Both gateways use OpenClaw's default port unless configured otherwise. Give
  one instance a different `gateway.port` before running both gateways at
  once.
- Shell completion is bound to the base command names, and
  `clawctl-pwsh-exec completion --install` or `--uninstall` edits the same
  profile block as the base `clawctl`. Manage completion from the base package
  only.

#### Remove a patch

Remove every patch when you finish with it. Tear down its isolated session
while its commands still exist, then unregister it with the same
`-Architecture` you deployed:

```powershell
clawctl-pwsh-exec teardown --force
.\scripts\Deploy-LocalPackage.ps1 -Unregister -Patch pwsh-exec
```

Teardown removes the patch's isolated session, its data, any gateway logon
task, and its setup state. `-Unregister -Patch pwsh-exec` then removes only
that registration and preserves its app data. If you unregister first, the
isolated session stays behind; redeploy the same patch to reach it again, then
tear it down. `-Unregister` without `-Patch` never removes patched
registrations. To find patches you have not removed, run
`Get-AppxPackage -Name 'OpenClawFoundation.OpenClawGateway-*'`.

## Compose, sign, and install an artifact

Use this lane when an MSIX itself, rather than a live layout, is the thing to
exercise:

```powershell
$unsignedArtifacts = '.\artifacts\local-msix-input'
.\scripts\Build-LocalMSIX.ps1 `
  -Architecture x64 `
  -OutputDirectory "$unsignedArtifacts\x64"
```

Without `-PayloadDirectory`, composition uses `gh` to resolve the latest
successful `gateway-msix.yml` payload from `main` and composes that run. It
keeps downloaded payloads in `artifacts\local-msix\payloads\<architecture>` and
downloads only when the latest run is not the cached one. `-PayloadRunId <id>`
pins a run instead; when that run is cached, composition needs no GitHub
access, so pin the cached run to compose offline. If the latest run cannot be
resolved, the script fails and names both options rather than composing a
cached payload that may be stale. `-RefreshPayload` downloads the selected run
again. A downloaded payload becomes the cached selection only after
composition succeeds; a payload that fails composition is discarded, and each
successful run reclaims downloads an interrupted run left behind, including an
interrupted first download. This
cache is separate from the deployment caches, and nothing registered links to
it, so deleting the `payloads` directory only costs the next run a download.
`-PayloadDirectory <path>` instead uses a prepared payload and cannot be
combined with `-PayloadRunId` or `-RefreshPayload`. `-NodeArchivePath <path>`
supplies a matching already-downloaded Node archive.

Runs in one checkout share `content\openclaw` and the payload cache, so a run
holds `artifacts\local-msix\.lock` until it exits and a second run in the same
checkout fails immediately, naming that lock. The script writes a new output
directory under `artifacts\local-msix\<architecture>\<version>` and builds an
unsigned MSIX. It refuses to reuse an existing output directory and tells you
to select another `-PackageVersion` or `-OutputDirectory`.

Test-sign the artifact in a separate step. The signing script requires Windows,
the Windows SDK `signtool.exe`, an artifact directory containing architecture
subdirectories and/or `bundle`, and an output directory. Pass the parent of the
explicit architecture output above; the signer searches directly inside each
architecture directory and does not recurse into version subdirectories:

```powershell
$signedArtifacts = '.\artifacts\local-msix-signed'
.\scripts\Sign-TestMSIX.ps1 `
  -ArtifactsDirectory $unsignedArtifacts `
  -OutputDirectory $signedArtifacts
```

It copies signable MSIX/MSIX bundle inputs to the output directory and signs
them using a temporary runner-local certificate. It also exports that
certificate beside each signed package and records its thumbprint in
`msix-metadata.json`; it does not add the certificate to a trust store. This is
test signing, not an official release-signing path.

Before installing the signed output, switch away from a base loose
registration through the repository-owned transition. Patched registrations use
another package name and do not block the install:

```powershell
.\scripts\Deploy-LocalPackage.ps1 -Unregister
```

`-Unregister` removes only this checkout's local registration while preserving
its app data and caches. It refuses to remove an MSIX-installed package or a
layout owned by another checkout; act from the owning checkout instead. Do not
replace this with a broad `Remove-AppxPackage` recipe. The inverse transition
is also explicit: use `-ReplaceExistingInstall` only when replacing an
installed package with a loose layout and accepting the data-loss boundary.

The self-signed test certificate must be trusted before Windows will install
the package. From the repository root in an **elevated PowerShell 7 window**,
verify that the exported certificate, signed package, and metadata all name the
same thumbprint before importing the certificate. Remove only the certificate
imported by this test, even when installation fails:

```powershell
$signedDirectory = '.\artifacts\local-msix-signed\x64'
$package = @(Get-ChildItem $signedDirectory -Filter '*.msix' -File)
if ($package.Count -ne 1) { throw "Expected one signed x64 MSIX; found $($package.Count)." }

$certificatePath = Join-Path $signedDirectory 'OpenClawGateway-test-signing.cer'
$metadata = Get-Content (Join-Path $signedDirectory 'msix-metadata.json') -Raw |
  ConvertFrom-Json
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
  (Resolve-Path $certificatePath).Path
)
$signature = Get-AuthenticodeSignature $package[0].FullName
if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
    $metadata.signingCertificateThumbprint -ne $certificate.Thumbprint) {
  throw 'The package, certificate, and metadata signing identities do not match.'
}

$trustedCertificate = Import-Certificate `
  -FilePath $certificatePath `
  -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
try {
  Add-AppxPackage -Path $package[0].FullName
}
finally {
  Remove-Item `
    -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($trustedCertificate.Thumbprint)" `
    -Force `
    -ErrorAction SilentlyContinue
}
```

## Checks that match the change

### Agent context guidance

Run `.\scripts\Test-GatewayIsolationPlugin.Tests.ps1` for the Windows Launcher
plugin's output, platform/report gates, staging, and payload inspection.

For actual prompt coverage, use a prepared, dependency-complete OpenClaw
application directory containing this checkout's plugin under
`dist\extensions\gateway-isolation`:

```powershell
.\scripts\Test-GatewayIsolationContext.ps1 -OpenClawDirectory .\payload\x64\app
```

This Windows-only lane verifies the application's build identity against
`release-policy.json` and rejects stale plugin files. It does not download,
build, install, or change the input application. Its disposable profile and
workspace use synthetic data, a stripped child environment, a loopback-only
fake provider on an allocated port, and only the `read` tool. No external model,
real account, package registration, or scheduled task is involved.

The fixture captures actual embedded-runner requests before the first tool,
through a forced context-overflow/compaction retry, on the next turn, and in
new, subagent-key, and cron-key sessions. It checks the complete instruction
block in both system and user context rather than just hook registration, plus plugin/hook opt-outs and
unchanged user configuration and instruction files. It does not schedule cron
jobs, spawn remote agents, evaluate model obedience, or prove attachment
delivery/user-side filesystem access.

The plugin uses `before_prompt_build` with `appendSystemContext` and
`prependContext`, both derived from the same static instructions. System context
states the host constraints at instruction priority; real-model testing showed
that user context alone could reach the model yet still be ignored. The user
interaction rule covers both launching and offering an unusable local dialog;
validate proposed next steps as well as tool calls in real-model scenarios. The user
copy remains a compatibility fallback because the approved v2026.9.4 runtime
can replace system-context additions on runtime-only events. Remove that
fallback only after the selected runtime preserves system context through
those events and the request-boundary proof covers them. Until then,
this lane does not synthesize those events or qualify external CLI/realtime
backends. Raw-model and settled-finalization operations omit prompt hooks.
Do not treat successful registration as universal context coverage. Repeat
the request-boundary proof when the approved runtime changes.

### Native dependency redirect

Run `.\scripts\Test-NativeRedirect.Tests.ps1` when you change
`src\OpenClaw.Launcher\node\native-redirect.mjs`. It requires Node.js and runs
`tests\node\native-redirect.test.mjs` with `node --test`. Each test starts
Node.js with the preload against temporary application and staged roots, then
checks where CommonJS, ESM, child-process, and worker-thread resolution lands.
It also checks the preload's `NODE_OPTIONS` entry and its startup failure when
`module.registerHooks()` is unavailable. The suite creates only temporary
fixtures. It never touches an isolated session, a package registration, or
installed OpenClaw data.

The loose-registration layout does not exercise the redirect. Its `app`
directory is a junction to the payload, and Node.js resolves modules through
real paths, which never fall under the configured application root. To prove
redirection inside the isolated session, use a package installed as described
in [Compose, sign, and install an artifact](#compose-sign-and-install-an-artifact).

### Managed code and packaging

For ordinary managed-code changes, use the contributor quality and test lanes:

```powershell
.\scripts\Test-DotNetQuality.ps1
dotnet test .\OpenClaw.Gateway.MSIX.slnx --configuration Release --no-restore
```

Run PowerShell policy suites by owning surface rather than indiscriminately:
`Test-SigningInputs.Tests.ps1` for signing inputs,
`Test-NodeRuntimeInputs.Tests.ps1` for Node runtime inputs,
`Test-PackagingRelevance.Tests.ps1`, `Test-OpenClawCacheKey.Tests.ps1`, and
`Test-OpenClawPackage.Tests.ps1` for payload/package workflow behavior,
`Test-Measure-Coverage.Tests.ps1` for the diagnostic coverage-reporting script,
`Test-Copy-PayloadTree.Tests.ps1` for the payload tree copy used by
`Build-Payload.ps1` and `Build-MSIX.ps1`,
`Test-MSIXReleaseIdentity.Tests.ps1` and
`Test-WorkflowPackageVersion.Tests.ps1` for release/version identity,
`Test-Deploy-LocalPackage.Tests.ps1` for the loose-registration flow, and
`Test-Sign-TestMSIX.Tests.ps1` for test signing. The Node input suite requires
Node.js and npm; it uses a dependency-free local fixture.

When changing command-line parsing, help, version output, startup, trimming, or
NativeAOT-sensitive code, also run:

```powershell
.\scripts\Test-NativeAotCli.Tests.ps1
```

JIT build and xUnit results are insufficient: the test host cannot expose the
native `argv[0]` alias/root-command behavior, and a successful publish does not
prove the trimmed executable runs. The gate publishes the NativeAOT scenario
driver into a fixture-owned temporary directory, runs it as `clawctl.exe`, and
removes that directory. Its collaborators, including diagnostics, are
fixture-owned so the test cannot append to the real
`%LOCALAPPDATA%\OpenClawGatewayMSIX` log or launch a real process.
