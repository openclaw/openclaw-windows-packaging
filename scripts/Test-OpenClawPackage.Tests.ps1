[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Test-OpenClawPackage.ps1'
$commit = '1111111111111111111111111111111111111111'
$testRoot = Join-Path $env:TEMP (
    "openclaw-package-verification-$([guid]::NewGuid().ToString('N'))"
)

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

New-Item -Path $testRoot -ItemType Directory | Out-Null
try {
    $packagePath = Join-Path $testRoot 'openclaw.tgz'
    $metadataPath = Join-Path $testRoot 'source.json'
    Set-Content -LiteralPath $packagePath -Value 'package contents'
    $hash = (
        Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    [ordered]@{
        repository = 'https://github.com/openclaw/openclaw'
        requestedRef = 'old-ref'
        resolvedCommit = $commit
        packageVersion = '1.2.3'
        nodeVersion = '24.20.0'
        packageSha256 = $hash
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath $metadataPath -Encoding utf8

    & $scriptPath `
        -PackageDirectory $testRoot `
        -ExpectedCommit $commit `
        -ExpectedVersion '1.2.3' `
        -RequestedRef 'current-ref'
    $verified = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($verified.requestedRef -cne 'current-ref') {
        throw 'Package verification did not refresh the requested ref.'
    }

    Add-Content -LiteralPath $packagePath -Value 'corruption'
    Assert-Fails -MessagePattern 'SHA-256 verification' -Action {
        & $scriptPath `
            -PackageDirectory $testRoot `
            -ExpectedCommit $commit `
            -ExpectedVersion '1.2.3' `
            -RequestedRef 'current-ref'
    }

    Set-Content -LiteralPath $packagePath -Value 'package contents'
    $verified.packageSha256 = (
        Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $verified |
        ConvertTo-Json |
        Set-Content -LiteralPath $metadataPath -Encoding utf8
    Assert-Fails -MessagePattern 'does not match' -Action {
        & $scriptPath `
            -PackageDirectory $testRoot `
            -ExpectedCommit '2222222222222222222222222222222222222222' `
            -ExpectedVersion '1.2.3' `
            -RequestedRef 'current-ref'
    }
    Assert-Fails -MessagePattern 'version does not match' -Action {
        & $scriptPath `
            -PackageDirectory $testRoot `
            -ExpectedCommit $commit `
            -ExpectedVersion '1.2.4' `
            -RequestedRef 'current-ref'
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'OpenClaw package verification tests passed.'
