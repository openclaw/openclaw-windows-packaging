[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Get-MSIXReleaseIdentity.ps1'
$workflowVersionScriptPath = Join-Path $PSScriptRoot 'Get-WorkflowPackageVersion.ps1'

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
    -PackageVersion '2026.9.4.1000' `
    -ReleaseTag 'v2026.9.4-msix.0'
Assert-Identity `
    -GatewayTag 'v2026.7.12' `
    -MSIXRevision 0 `
    -PackageVersion '2026.7.12.1000' `
    -ReleaseTag 'v2026.7.12-msix.0'
Assert-Identity `
    -GatewayTag 'v2026.7.2' `
    -MSIXRevision 0 `
    -PackageVersion '2026.7.2.1000' `
    -ReleaseTag 'v2026.7.2-msix.0'
Assert-Identity `
    -GatewayTag 'v2026.7.1' `
    -MSIXRevision 1 `
    -PackageVersion '2026.7.1.1001' `
    -ReleaseTag 'v2026.7.1-msix.1'
Assert-Identity `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 0 `
    -PackageVersion '2026.7.1.2000' `
    -ReleaseTag 'v2026.7.1-2-msix.0'
Assert-Identity `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 1 `
    -PackageVersion '2026.7.1.2001' `
    -ReleaseTag 'v2026.7.1-2-msix.1'

$gatewayCorrection = & $scriptPath `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 0
$packagingCorrection = & $scriptPath `
    -GatewayTag 'v2026.7.1-2' `
    -MSIXRevision 1
$nextGatewayCorrection = & $scriptPath `
    -GatewayTag 'v2026.7.1-3' `
    -MSIXRevision 0
$nextGatewayPatch = & $scriptPath `
    -GatewayTag 'v2026.7.2' `
    -MSIXRevision 0
if (
    [version]$packagingCorrection.PackageVersion -le
        [version]$gatewayCorrection.PackageVersion -or
    [version]$nextGatewayCorrection.PackageVersion -le
        [version]$packagingCorrection.PackageVersion -or
    [version]$nextGatewayPatch.PackageVersion -le
        [version]$nextGatewayCorrection.PackageVersion
) {
    throw 'Derived MSIX versions do not preserve release ordering.'
}

Assert-Fails -MessagePattern 'stable OpenClaw release tag' -Action {
    & $scriptPath -GatewayTag 'v2026.9.4-beta.1' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'month must be between 1 and 12' -Action {
    & $scriptPath -GatewayTag 'v2026.13.1' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'patch must be between 0 and 65534' -Action {
    & $scriptPath -GatewayTag 'v2026.9.65535' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'correction suffix must be between 2 and 64' -Action {
    & $scriptPath -GatewayTag 'v2026.9.4-1' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'correction suffix must be between 2 and 64' -Action {
    & $scriptPath -GatewayTag 'v2026.9.4-65' -MSIXRevision 0
}
Assert-Fails -MessagePattern 'MSIXRevision must be between 0 and 999' -Action {
    & $scriptPath -GatewayTag 'v2026.9.4' -MSIXRevision 1000
}
Assert-Identity `
    -GatewayTag 'v2026.9.4-64' `
    -MSIXRevision 999 `
    -PackageVersion '2026.9.4.64999' `
    -ReleaseTag 'v2026.9.4-64-msix.999'

$maximumIdentity = & $scriptPath `
    -GatewayTag 'v2026.9.4-64' `
    -MSIXRevision 999
$validatedMaximumVersion = & $workflowVersionScriptPath `
    -RunNumber 1 `
    -RunAttempt 1 `
    -ReleaseVersion $maximumIdentity.PackageVersion
if ($validatedMaximumVersion -ne '2026.9.4.64999') {
    throw 'The maximum derived identity did not pass workflow validation.'
}

Write-Host 'MSIX release-identity tests passed.'
