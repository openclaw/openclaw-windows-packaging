<#
.SYNOPSIS
Builds and registers a local development OpenClaw Gateway package, ready to run.
.DESCRIPTION
Takes a clean checkout to a registered, runnable OpenClaw.Gateway package without
producing, signing, or installing an MSIX. It acquires the OpenClaw payload and
the bundled Node.js and MXC runtimes, publishes the NativeAOT launcher and
session host, assembles a Developer Mode layout, and registers it with
Add-AppxPackage -Register.

The command is idempotent. Re-running with nothing changed reports that the
package is already up to date and does nothing; re-running after a source or
payload change rebuilds only what changed and re-registers. Downloads are cached
under artifacts\local-package, and the expanded application is linked rather
than copied, so repeat runs do not re-fetch or duplicate hundreds of megabytes.

Requires Developer Mode. The registered package reads its files from the
repository, so deleting artifacts\local-package or the checkout breaks it until
the command runs again. This is a development build and is not an
official-signing input; use Build-LocalMSIX.ps1 for a verified unsigned MSIX.
.PARAMETER Architecture
Build and register x64 or arm64. Defaults to this device's native architecture;
pass x64 explicitly to build an emulated x64 layout on an ARM64 device.
.PARAMETER PayloadDirectory
Use an existing payload root containing app and payload-metadata.json. The
directory is read directly and never modified, and no GitHub access is needed.
Pass it on every run; it is not remembered.
.PARAMETER PayloadRunId
Use a specific successful workflow run instead of the latest one on main. A
matching cached payload is reused rather than downloaded again.
.PARAMETER RefreshPayload
Download the payload again instead of reusing the cache. The previous payload
is kept until the new one is registered successfully.
.PARAMETER ReplaceExistingInstall
Take over an existing install this checkout does not own: an MSIX-installed
OpenClaw.Gateway, or a local registration from another checkout. Windows cannot
replace a packaged install with a local layout and cannot preserve its app data
across that switch, so this is never done implicitly.
.PARAMETER Force
Re-register even when nothing changed.
.PARAMETER SkipSetup
Skip the final `clawctl setup` that extracts the bundled Node.js runtime. The
package is registered but not runnable until setup is run once.
.PARAMETER Patch
Deploy a side-by-side development identity instead of the base package; this
is the recommended way to iterate. It is a local inner-loop affordance for
keeping several development and test deployments registered at once, not a
release identity. Name it for the work. With -Patch pwsh-exec
the package is OpenClawFoundation.OpenClawGateway-pwsh-exec, its commands are
openclaw-pwsh-exec and clawctl-pwsh-exec, and its state lives under
artifacts\local-package\patches\pwsh-exec. It gets its own isolated session and
app data and never touches the base registration. Use 1 to 15 letters, digits,
or hyphens; the value is lowercased. Its help and error guidance still names
clawctl and openclaw; run the patched commands instead. When finished, run
`clawctl-pwsh-exec teardown --force`, then -Unregister with the same -Patch.
.PARAMETER Unregister
Remove the local development registration and exit. Cached payloads and
runtimes are kept, and the package's app data is preserved. Run this for a base
registration before installing a released package: Windows will not replace a
loose registration with a packaged install, regardless of version.
.EXAMPLE
.\scripts\Deploy-LocalPackage.ps1 -Patch pwsh-exec
Iterate beside the base package as openclaw-pwsh-exec and clawctl-pwsh-exec;
later runs reuse the cache.
.EXAMPLE
clawctl-pwsh-exec teardown --force && .\scripts\Deploy-LocalPackage.ps1 -Unregister -Patch pwsh-exec
Remove a finished patch: its isolated session first, then its registration.
.EXAMPLE
.\scripts\Deploy-LocalPackage.ps1
Register the base identity, for work on its own aliases, completion, or
loose/MSIX transitions.
.EXAMPLE
.\scripts\Deploy-LocalPackage.ps1 -RefreshPayload
Pick up a newer OpenClaw payload from the latest successful main workflow run.
.EXAMPLE
.\scripts\Deploy-LocalPackage.ps1 -PayloadDirectory E:\payloads\x64
Register from a prepared payload without contacting GitHub.
.EXAMPLE
.\scripts\Deploy-LocalPackage.ps1 -Unregister
Remove the base local registration.
#>
[CmdletBinding(DefaultParameterSetName = 'Deploy')]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$Patch,

    [Parameter(ParameterSetName = 'Deploy')]
    [string]$PayloadDirectory,

    [Parameter(ParameterSetName = 'Deploy')]
    [long]$PayloadRunId,

    [Parameter(ParameterSetName = 'Deploy')]
    [switch]$RefreshPayload,

    [Parameter(ParameterSetName = 'Deploy')]
    [switch]$ReplaceExistingInstall,

    [Parameter(ParameterSetName = 'Deploy')]
    [switch]$Force,

    [Parameter(ParameterSetName = 'Deploy')]
    [switch]$SkipSetup,

    [Parameter(Mandatory, ParameterSetName = 'Unregister')]
    [switch]$Unregister
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'LocalPackage.psm1') -Force

# Forward -Patch and -Architecture only when they were supplied, so an
# explicitly empty -Patch reaches the module's validation instead of silently
# selecting the base, and the module owns the native-architecture default.
$identityArguments = @{}
if ($PSBoundParameters.ContainsKey('Patch')) { $identityArguments.Patch = $Patch }
if ($PSBoundParameters.ContainsKey('Architecture')) { $identityArguments.Architecture = $Architecture }

if ($Unregister) {
    Remove-LocalPackageRegistration -RepositoryRoot $repositoryRoot @identityArguments
    return
}

Invoke-LocalPackageDeployment `
    -RepositoryRoot $repositoryRoot `
    -PayloadDirectory $PayloadDirectory `
    -PayloadRunId $PayloadRunId `
    -RefreshPayload:$RefreshPayload `
    -ReplaceExistingInstall:$ReplaceExistingInstall `
    -Force:$Force `
    -SkipSetup:$SkipSetup `
    @identityArguments |
    Out-Null
