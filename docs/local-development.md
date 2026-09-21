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
| Iterate on the packaged application without producing an MSIX | `.\scripts\Deploy-LocalPackage.ps1` publishes and registers a Developer Mode layout. This is the normal inner loop. |
| Produce an unsigned artifact | `.\scripts\Build-LocalMSIX.ps1` composes an unsigned MSIX from a payload; it does not install it. |
| Make a local artifact installable for testing | `.\scripts\Sign-TestMSIX.ps1` signs the output separately with a temporary test certificate. |
| Install a signed local artifact | After removing a loose registration with the deployment script, install the signed package with `Add-AppxPackage -Path <signed-msix>`. |

Loose registration and an installed MSIX are mutually exclusive for the
`OpenClaw.Gateway` identity. Windows cannot preserve packaged app data while
switching between them. Treat a transition as deliberate, not an update.

## Fast, loose-registration inner loop

From a clean checkout, register an x64 layout and prepare the runtime:

```powershell
.\scripts\Deploy-LocalPackage.ps1 -Architecture x64
```

The script resolves a payload, stages MXC and the bundled Node.js runtime,
publishes both the NativeAOT `openclaw.exe` launcher and the NativeAOT session
host, assembles the layout, registers it with `Add-AppxPackage -Register`, and
runs `clawctl setup`. The layout lives under
`artifacts\local-package\<architecture>\layout`.

Use options for a concrete reason:

| Option | Use it when |
| --- | --- |
| `-Architecture x64` or `arm64` | The target architecture is runnable on this device. `x64` is the default. |
| `-PayloadDirectory <path>` | You already have an expanded payload containing `app` and `payload-metadata.json`, or need an offline/reproducible iteration. It is read directly, is not modified, makes no GitHub request, and must be supplied on every run. |
| no payload option | You want the script to resolve the latest successful `main` payload workflow. |
| `-PayloadRunId <id>` | You need a specific successful workflow payload instead of the latest one. Use the current branch's successful workflow run when that branch changes packaged application content. |
| `-RefreshPayload` | A cached payload must be downloaded again; the previous payload remains available until the replacement registers successfully. |
| `-Force` | You intentionally need to re-register although the recorded inputs have not changed. |
| `-SkipSetup` | You only need the registration. Run `clawctl setup` before attempting to run OpenClaw, because the bundled Node.js runtime has not yet been extracted. |
| `-ReplaceExistingInstall` | You explicitly accept removal of an installed MSIX or a registration owned by another checkout. This is destructive because app data cannot survive the loose/installed transition. |

The command is idempotent, not timestamp-driven. `LocalPackage.psm1` records a
fingerprint of the relevant inputs, including the SHA-256 content hash of the
published launcher. A publish can refresh file timestamps without changing
code, so timestamps would spuriously redeploy the layout. With an unchanged
fingerprint, the command reports that the package is current; source or payload
changes rebuild only the affected work.

**Never delete `artifacts\local-package` or the checkout while it is
registered.** The Developer Mode package reads those live files, so either
action breaks it until redeployment. The expanded payload is linked rather than
copied, which is why repeat deploys avoid duplicating it.

### Verify the registered package

```powershell
Get-AppxPackage -Name OpenClaw.Gateway |
  Select-Object PackageFullName, InstallLocation, IsDevelopmentMode, SignatureKind

clawctl --version
clawctl status
```

For a loose layout, `InstallLocation` should be the checkout's
`artifacts\local-package\<architecture>\layout` and `IsDevelopmentMode` should
be `True`. `clawctl --version` prints the package version and packaging commit
alongside the payload version and commit; compare these to the payload and
checkout you intended to use. `clawctl status` is an active status probe: it
asks the backend to start the recorded provision, but does not create a
replacement provision or start the gateway.

After a successful loose deployment, expect the registration to report the
current checkout's `artifacts\local-package\<architecture>\layout`,
`IsDevelopmentMode` as `True`, and `SignatureKind` as `None`. The packaging
commit from `clawctl --version` must match the checkout you deployed; the
payload commit must match the payload selected by the deployment script. A
mismatch means the command is reaching another registration or the wrong
payload, even if the deployment command itself succeeded.

## Compose, sign, and install an artifact

Use this lane when an MSIX itself, rather than a live layout, is the thing to
exercise:

```powershell
$unsignedArtifacts = '.\artifacts\local-msix-input'
.\scripts\Build-LocalMSIX.ps1 `
  -Architecture x64 `
  -OutputDirectory "$unsignedArtifacts\x64"
```

Without `-PayloadDirectory`, composition uses `gh` to resolve and download the
latest successful `gateway-msix.yml` payload from `main`; use
`-PayloadRunId <id>` to pin that selection. `-PayloadDirectory <path>` instead
uses a prepared payload, and `-NodeArchivePath <path>` supplies a matching
already-downloaded Node archive. The script writes a new output directory under
`artifacts\local-msix\<architecture>\<version>`, builds an unsigned MSIX, then
removes its temporary work directory. It refuses to reuse an existing output
directory and tells you to select another `-PackageVersion` or
`-OutputDirectory`.

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

Before installing the signed output, switch away from a loose registration
through the repository-owned transition:

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
