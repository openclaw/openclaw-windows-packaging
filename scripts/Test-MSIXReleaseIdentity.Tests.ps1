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
    -PackageVersion '2026.9.4.0' `
    -ReleaseTag 'v2026.9.4-msix.0'
Assert-Identity `
    -GatewayTag 'v2026.7.12' `
    -MSIXRevision 0 `
    -PackageVersion '2026.7.12.0' `
    -ReleaseTag 'v2026.7.12-msix.0'
Assert-Identity `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 2 `
    -PackageVersion '2026.7.1.2' `
    -ReleaseTag 'v2026.7.1-2-msix.2'
Assert-Identity `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 3 `
    -PackageVersion '2026.7.1.3' `
    -ReleaseTag 'v2026.7.1-2-msix.3'

$gatewayCorrection = & $scriptPath `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 2
$packagingCorrection = & $scriptPath `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 3
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
Assert-Fails -MessagePattern 'patch must be between 0 and 65535' -Action {
    & $scriptPath -GatewayTag 'v2026.9.65536' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'correction must be between 0 and 65535' -Action {
    & $scriptPath -GatewayTag 'v2026.9.4-65536' -MSIXRevision 65535
}
Assert-Fails -MessagePattern 'MSIXRevision must be between 0 and 65535' -Action {
    & $scriptPath -GatewayTag 'v2026.9.4' -MSIXRevision 65536
}
Assert-Fails -MessagePattern 'must be at least the Gateway correction 2' -Action {
    & $scriptPath -GatewayTag 'v2026.7.1-2' -MSIXRevision 1
}

Write-Host 'MSIX release-identity tests passed.'
