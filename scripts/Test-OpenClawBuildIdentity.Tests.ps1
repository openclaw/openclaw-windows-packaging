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
        [string]$GatewayBuildId,

        [string]$ControlUiBuildId,

        [switch]$OmitControlUiBuildId
    )

    $root = Join-Path $testRoot $Name
    $distDirectory = Join-Path $root 'dist'
    $controlUiDirectory = Join-Path $distDirectory 'control-ui'
    $assetsDirectory = Join-Path $controlUiDirectory 'assets'
    New-Item -Path $assetsDirectory -ItemType Directory -Force | Out-Null

    @{ buildId = $GatewayBuildId } |
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
        -GatewayBuildId 'release-build-a' `
        -ControlUiBuildId 'release-build-a'
    & $scriptPath -OpenClawDirectory $matching

    # Missing UI identity is unsafe because packaging could not prove which
    # dashboard build will connect to the Gateway.
    $missing = New-BuildFixture `
        -Name 'missing' `
        -GatewayBuildId 'release-build-a' `
        -OmitControlUiBuildId
    Assert-Fails -MessagePattern 'Control UI build identity is missing' -Action {
        & $scriptPath -OpenClawDirectory $missing
    }

    # A separately rebuilt dashboard must fail even when both artifacts are
    # otherwise complete, reproducing the release-only regression.
    $mismatched = New-BuildFixture `
        -Name 'mismatched' `
        -GatewayBuildId 'release-build-a' `
        -ControlUiBuildId 'release-build-b'
    Assert-Fails -MessagePattern 'OpenClaw build identity mismatch' -Action {
        & $scriptPath -OpenClawDirectory $mismatched
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host 'OpenClaw build identity validation tests passed.'
