[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Get-MSIXReleaseIdentity.ps1'

function Assert-Identity {
    param(
        [Parameter(Mandatory)]
        [string]$GatewayTag,

        [Parameter(Mandatory)]
        [int]$MSIXRevision,

        [Parameter(Mandatory)]
        [string]$PackageVersion,

        [Parameter(Mandatory)]
        [string]$ReleaseTag
    )

    $identity = & $scriptPath `
        -GatewayTag $GatewayTag `
        -MSIXRevision $MSIXRevision
    if ($identity.PackageVersion -ne $PackageVersion) {
        throw (
            "Expected $GatewayTag revision $MSIXRevision to produce " +
            "$PackageVersion; received $($identity.PackageVersion)."
        )
    }
    if ($identity.ReleaseTag -ne $ReleaseTag) {
        throw (
            "Expected $GatewayTag revision $MSIXRevision to produce " +
            "$ReleaseTag; received $($identity.ReleaseTag)."
        )
    }
    if ($identity.ReleaseVersion -ne $ReleaseTag.Substring(1)) {
        throw 'ReleaseVersion did not match the release tag without its v prefix.'
    }
    if (-not $identity.PackageVersion.EndsWith('.0')) {
        throw 'The MSIX revision component must remain zero for Store compatibility.'
    }
}

function Assert-Fails {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw (
                "Expected failure matching '$MessagePattern'; received: " +
                $_.Exception.Message
            )
        }
        return
    }

    throw "Expected failure matching '$MessagePattern', but the action succeeded."
}

Assert-Identity `
    -GatewayTag 'v2026.9.4' `
    -MSIXRevision 0 `
    -PackageVersion '2026.9.4000.0' `
    -ReleaseTag 'v2026.9.4-msix.0'
Assert-Identity `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 0 `
    -PackageVersion '2026.7.1200.0' `
    -ReleaseTag 'v2026.7.1-2-msix.0'
Assert-Identity `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 1 `
    -PackageVersion '2026.7.1201.0' `
    -ReleaseTag 'v2026.7.1-2-msix.1'

$gatewayCorrection = & $scriptPath `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 0
$packagingCorrection = & $scriptPath `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 1
$nextGatewayPatch = & $scriptPath `
    -GatewayTag 'v2026.7.2' `
    -MSIXRevision 0
if (
    [version]$packagingCorrection.PackageVersion -le
        [version]$gatewayCorrection.PackageVersion -or
    [version]$nextGatewayPatch.PackageVersion -le
        [version]$packagingCorrection.PackageVersion
) {
    throw 'Derived MSIX versions do not preserve release ordering.'
}

Assert-Fails -MessagePattern 'stable OpenClaw release tag' -Action {
    & $scriptPath -GatewayTag 'v2026.9.4-beta.1' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'month must be between 1 and 12' -Action {
    & $scriptPath -GatewayTag 'v2026.13.1' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'patch must be between 0 and 64' -Action {
    & $scriptPath -GatewayTag 'v2026.9.65' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'correction must be between 0 and 9' -Action {
    & $scriptPath -GatewayTag 'v2026.9.4-10' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'MSIXRevision must be between 0 and 99' -Action {
    & $scriptPath -GatewayTag 'v2026.9.4' -MSIXRevision 100
}

Write-Host 'MSIX release-identity tests passed.'
