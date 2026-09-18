[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Test-OpenClawBuildIdentity.ps1'
$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "openclaw-build-identity-$([guid]::NewGuid().ToString('N'))"

function New-BuildFixture {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [hashtable]$GatewayBuildInfo,

        [string]$ControlUiBuildId,

        [switch]$OmitControlUiBuildId
    )

    $root = Join-Path $testRoot $Name
    $distDirectory = Join-Path $root 'dist'
    $controlUiDirectory = Join-Path $distDirectory 'control-ui'
    $assetsDirectory = Join-Path $controlUiDirectory 'assets'
    New-Item -Path $assetsDirectory -ItemType Directory -Force | Out-Null

    $GatewayBuildInfo |
        ConvertTo-Json |
        Set-Content (Join-Path $distDirectory 'build-info.json') -Encoding utf8

    if ($OmitControlUiBuildId) {
        'const CACHE_NAME = "openclaw-control-ui";' |
            Set-Content (Join-Path $controlUiDirectory 'sw.js') -Encoding utf8
    }
    else {
        $encodedBuildId = $ControlUiBuildId | ConvertTo-Json -Compress
        "const EMBEDDED_CACHE_VERSION = $encodedBuildId;" |
            Set-Content (Join-Path $controlUiDirectory 'sw.js') -Encoding utf8
    }

    $clientBuildId = if ($null -eq $ControlUiBuildId) {
        ''
    }
    else {
        $ControlUiBuildId
    }
    "const buildInfo = { buildId: ""$clientBuildId"" };" |
        Set-Content (Join-Path $assetsDirectory 'app.js') -Encoding utf8
    [IO.File]::WriteAllText((Join-Path $assetsDirectory 'empty.js'), '')

    return $root
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

try {
    # A single OpenClaw build must emit one identity across all runtime and UI
    # artifacts consumed by the packaged dashboard handshake.
    $matching = New-BuildFixture `
        -Name 'matching' `
        -GatewayBuildInfo @{ buildId = 'release-build-a' } `
        -ControlUiBuildId 'release-build-a'
    & $scriptPath -OpenClawDirectory $matching

    # Missing UI identity is unsafe because packaging could not prove which
    # dashboard build will connect to the Gateway.
    $missing = New-BuildFixture `
        -Name 'missing' `
        -GatewayBuildInfo @{ buildId = 'release-build-a' } `
        -OmitControlUiBuildId
    Assert-Fails -MessagePattern 'Control UI build identity is missing' -Action {
        & $scriptPath -OpenClawDirectory $missing
    }

    # A separately rebuilt dashboard must fail even when both artifacts are
    # otherwise complete, reproducing the release-only regression.
    $mismatched = New-BuildFixture `
        -Name 'mismatched' `
        -GatewayBuildInfo @{ buildId = 'release-build-a' } `
        -ControlUiBuildId 'release-build-b'
    Assert-Fails -MessagePattern 'OpenClaw build identity mismatch' -Action {
        & $scriptPath -OpenClawDirectory $mismatched
    }

    $legacyBuildInfo = @{
        version = '2026.7.33'
        commit = 'b60a4e9fa97cddf1869a1866d879b3051783cf12'
    }
    $legacyBuildId = '2026.7.33-b60a4e9fa97c'
    $legacy = New-BuildFixture `
        -Name 'legacy-matching' `
        -GatewayBuildInfo $legacyBuildInfo `
        -ControlUiBuildId $legacyBuildId
    & $scriptPath -OpenClawDirectory $legacy

    $normalized = New-BuildFixture `
        -Name 'legacy-normalized' `
        -GatewayBuildInfo @{
            version = '2026.7.33-beta.1+build.2'
            commit = $legacyBuildInfo.commit
        } `
        -ControlUiBuildId '2026.7.33-beta.1-build.2-b60a4e9fa97c'
    & $scriptPath -OpenClawDirectory $normalized

    $longVersion = "2026.7.33-$('a' * 90)"
    $truncated = New-BuildFixture `
        -Name 'legacy-truncated' `
        -GatewayBuildInfo @{
            version = $longVersion
            commit = $legacyBuildInfo.commit
        } `
        -ControlUiBuildId $longVersion.Substring(0, 96)
    & $scriptPath -OpenClawDirectory $truncated

    foreach ($identity in @(
        '2026.7.32-b60a4e9fa97c'
        '2026.7.33-0965053fe6b9'
    )) {
        $legacyMismatch = New-BuildFixture `
            -Name "legacy-mismatch-$identity" `
            -GatewayBuildInfo $legacyBuildInfo `
            -ControlUiBuildId $identity
        Assert-Fails -MessagePattern 'OpenClaw build identity mismatch' -Action {
            & $scriptPath -OpenClawDirectory $legacyMismatch
        }
    }

    $invalidBuildInfos = @(
        @{}
        @{ version = $legacyBuildInfo.version }
        @{ commit = $legacyBuildInfo.commit }
        @{ version = 'dev'; commit = $legacyBuildInfo.commit }
        @{ version = 2026; commit = $legacyBuildInfo.commit }
        @{ version = $legacyBuildInfo.version; commit = 'b60a4e9fa97c' }
        @{ version = $legacyBuildInfo.version; commit = ('z' * 40) }
        @{ version = $legacyBuildInfo.version; commit = $null }
    )
    for ($index = 0; $index -lt $invalidBuildInfos.Count; $index++) {
        $invalid = New-BuildFixture `
            -Name "invalid-metadata-$index" `
            -GatewayBuildInfo $invalidBuildInfos[$index] `
            -ControlUiBuildId $legacyBuildId
        Assert-Fails -MessagePattern 'Gateway build identity is missing' -Action {
            & $scriptPath -OpenClawDirectory $invalid
        }
    }

    foreach ($buildId in @($null, '', ' ', 'different-build')) {
        $explicitBuildInfo = $legacyBuildInfo.Clone()
        $explicitBuildInfo.buildId = $buildId
        $explicit = New-BuildFixture `
            -Name "explicit-build-id-$([guid]::NewGuid().ToString('N'))" `
            -GatewayBuildInfo $explicitBuildInfo `
            -ControlUiBuildId $legacyBuildId
        Assert-Fails `
            -MessagePattern 'Gateway build identity is missing|OpenClaw build identity mismatch' `
            -Action {
                & $scriptPath -OpenClawDirectory $explicit
            }
    }

    $legacyMissingUi = New-BuildFixture `
        -Name 'legacy-missing-ui' `
        -GatewayBuildInfo $legacyBuildInfo `
        -OmitControlUiBuildId
    Assert-Fails -MessagePattern 'Control UI build identity is missing' -Action {
        & $scriptPath -OpenClawDirectory $legacyMissingUi
    }

    foreach ($fixture in @($matching, $legacy)) {
        'const buildInfo = {};' |
            Set-Content `
                -LiteralPath (Join-Path $fixture 'dist\control-ui\assets\app.js') `
                -Encoding utf8
        Assert-Fails `
            -MessagePattern 'Control UI client bundle does not contain Gateway build identity' `
            -Action {
                & $scriptPath -OpenClawDirectory $fixture
            }
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host 'OpenClaw build identity validation tests passed.'
