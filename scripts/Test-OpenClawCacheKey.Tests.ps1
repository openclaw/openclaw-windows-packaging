[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Get-OpenClawCacheKey.ps1'
$commitA = '1111111111111111111111111111111111111111'
$commitB = '2222222222222222222222222222222222222222'
$packageHashA = 'a' * 64
$packageHashB = 'b' * 64
$testRoot = Join-Path $env:TEMP (
    "openclaw-cache-key-$([guid]::NewGuid().ToString('N'))"
)

function Assert-NotEqual {
    param(
        [Parameter(Mandatory)]
        [string]$Left,

        [Parameter(Mandatory)]
        [string]$Right,

        [Parameter(Mandatory)]
        [string]$Reason
    )

    if ($Left -ceq $Right) {
        throw "Expected different cache keys: $Reason"
    }
}

New-Item -Path $testRoot -ItemType Directory | Out-Null
try {
    $payloadScript = Join-Path $testRoot 'Build-Payload.ps1'
    Set-Content -LiteralPath $payloadScript -Value 'Write-Output first'

    $packageA = & $scriptPath -Layer package -Commit $commitA
    $packageARepeat = & $scriptPath -Layer package -Commit $commitA
    if ($packageA -cne $packageARepeat) {
        throw 'The same package inputs did not produce a stable cache key.'
    }

    $packageB = & $scriptPath -Layer package -Commit $commitB
    Assert-NotEqual $packageA $packageB 'distinct commits'

    $packageV2 = & $scriptPath `
        -Layer package `
        -Commit $commitA `
        -FormatVersion 2
    Assert-NotEqual $packageA $packageV2 'package cache format version'

    $payloadX64 = & $scriptPath `
        -Layer payload `
        -Commit $commitA `
        -Architecture x64 `
        -NodeVersion 24.20.0 `
        -PackageSha256 $packageHashA `
        -PayloadScriptPath $payloadScript
    $payloadArm64 = & $scriptPath `
        -Layer payload `
        -Commit $commitA `
        -Architecture arm64 `
        -NodeVersion 24.20.0 `
        -PackageSha256 $packageHashA `
        -PayloadScriptPath $payloadScript
    Assert-NotEqual $payloadX64 $payloadArm64 'distinct architectures'

    $payloadAfterPackageRebuild = & $scriptPath `
        -Layer payload `
        -Commit $commitA `
        -Architecture x64 `
        -NodeVersion 24.20.0 `
        -PackageSha256 $packageHashB `
        -PayloadScriptPath $payloadScript
    Assert-NotEqual `
        $payloadX64 `
        $payloadAfterPackageRebuild `
        'distinct package hashes'

    Set-Content -LiteralPath $payloadScript -Value 'Write-Output second'
    $payloadAfterEdit = & $scriptPath `
        -Layer payload `
        -Commit $commitA `
        -Architecture x64 `
        -NodeVersion 24.20.0 `
        -PackageSha256 $packageHashA `
        -PayloadScriptPath $payloadScript
    Assert-NotEqual $payloadX64 $payloadAfterEdit 'payload script edit'

    $payloadV2 = & $scriptPath `
        -Layer payload `
        -Commit $commitA `
        -Architecture x64 `
        -NodeVersion 24.20.0 `
        -PackageSha256 $packageHashA `
        -PayloadScriptPath $payloadScript `
        -FormatVersion 2
    Assert-NotEqual $payloadAfterEdit $payloadV2 'payload cache format version'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'OpenClaw cache key tests passed.'
