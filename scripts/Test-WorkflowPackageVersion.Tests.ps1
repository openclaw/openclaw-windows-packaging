[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Get-WorkflowPackageVersion.ps1'

function Assert-Version {
    param(
        [Parameter(Mandatory)]
        [long]$RunNumber,

        [Parameter(Mandatory)]
        [long]$RunAttempt,

        [Parameter(Mandatory)]
        [string]$Expected
    )

    $actual = & $scriptPath `
        -RunNumber $RunNumber `
        -RunAttempt $RunAttempt
    if ($actual -ne $Expected) {
        throw (
            "Expected run $RunNumber attempt $RunAttempt to produce " +
            "$Expected; received $actual."
        )
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

function Assert-ReleaseVersion {
    param(
        [Parameter(Mandatory)]
        [string]$ReleaseVersion,

        [Parameter(Mandatory)]
        [string]$Expected
    )

    $actual = & $scriptPath `
        -RunNumber 1 `
        -RunAttempt 1 `
        -ReleaseVersion $ReleaseVersion
    if ($actual -ne $Expected) {
        throw "Expected $ReleaseVersion to produce $Expected; received $actual."
    }
}

Assert-Version -RunNumber 1 -RunAttempt 1 -Expected '0.1.1.1'
Assert-Version -RunNumber 65534 -RunAttempt 1 -Expected '0.1.65534.1'
Assert-Version -RunNumber 65535 -RunAttempt 1 -Expected '0.2.0.1'
Assert-Version -RunNumber 65535 -RunAttempt 2 -Expected '0.2.0.2'
Assert-Version -RunNumber 65536 -RunAttempt 1 -Expected '0.2.1.1'
Assert-Version -RunNumber 131069 -RunAttempt 1 -Expected '0.2.65534.1'
Assert-Version -RunNumber 131070 -RunAttempt 1 -Expected '0.3.0.1'
Assert-Version -RunNumber 1 -RunAttempt 65534 -Expected '0.1.1.65534'

Assert-ReleaseVersion -ReleaseVersion '0.0.0.0' -Expected '0.0.0.0'
Assert-ReleaseVersion -ReleaseVersion '2026.9.4.0' -Expected '2026.9.4.0'

Assert-Fails -MessagePattern 'exactly four numeric components' -Action {
    & $scriptPath `
        -RunNumber 1 `
        -RunAttempt 1 `
        -ReleaseVersion '2026.9.1-beta.1'
}
Assert-Fails -MessagePattern 'greater than 65534' -Action {
    & $scriptPath `
        -RunNumber 1 `
        -RunAttempt 1 `
        -ReleaseVersion '2026.9.4.65535'
}

$maximumRunNumber = (65534L * 65535L) - 1L
Assert-Version `
    -RunNumber $maximumRunNumber `
    -RunAttempt 1 `
    -Expected '0.65534.65534.1'

Assert-Fails -MessagePattern 'greater than zero' -Action {
    & $scriptPath -RunNumber 0 -RunAttempt 1
}
Assert-Fails -MessagePattern 'between 1 and 65534' -Action {
    & $scriptPath -RunNumber 1 -RunAttempt 65535
}
Assert-Fails -MessagePattern 'exceeds the supported' -Action {
    & $scriptPath -RunNumber ($maximumRunNumber + 1L) -RunAttempt 1
}

$firstBoundaryVersion = [version](
    & $scriptPath -RunNumber 65534 -RunAttempt 1
)
$secondBoundaryVersion = [version](
    & $scriptPath -RunNumber 65535 -RunAttempt 1
)
if ($secondBoundaryVersion -le $firstBoundaryVersion) {
    throw 'The package version did not increase across the rollover boundary.'
}

Write-Host 'Workflow package-version tests passed.'
