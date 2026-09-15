[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Test-OpenClawBuildIdentity.ps1'
$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "openclaw-build-identity-$([guid]::NewGuid().ToString('N'))"
$expectedVersion = '2026.6.5'
$expectedCommit = 'a' * 40
$validationParameters = @{
    ExpectedPackageVersion = $expectedVersion
    ExpectedSourceCommit = $expectedCommit
}

function New-BuildFixture {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$GatewayBuildId,

        [string]$ControlUiBuildId,

        [switch]$OmitControlUiBuildId,

        [switch]$Legacy
    )

    $root = Join-Path $testRoot $Name
    $distDirectory = Join-Path $root 'dist'
    $controlUiDirectory = Join-Path $distDirectory 'control-ui'
    $assetsDirectory = Join-Path $controlUiDirectory 'assets'
    New-Item -Path $assetsDirectory -ItemType Directory -Force | Out-Null

    $buildInfo = @{
        version = $expectedVersion
        commit = $expectedCommit
        builtAt = '2026-09-15T07:00:00.000Z'
    }
    if (-not $Legacy) {
        $buildInfo.buildId = $GatewayBuildId
    }
    $buildInfo |
        ConvertTo-Json |
        Set-Content (Join-Path $distDirectory 'build-info.json') -Encoding utf8
    @{ name = 'openclaw'; version = $expectedVersion } |
        ConvertTo-Json |
        Set-Content (Join-Path $root 'package.json') -Encoding utf8

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
    & $scriptPath -OpenClawDirectory $matching @validationParameters

    # Missing UI identity is unsafe because packaging could not prove which
    # dashboard build will connect to the Gateway.
    $missing = New-BuildFixture `
        -Name 'missing' `
        -GatewayBuildId 'release-build-a' `
        -OmitControlUiBuildId
    Assert-Fails -MessagePattern 'Control UI build identity is missing' -Action {
        & $scriptPath -OpenClawDirectory $missing @validationParameters
    }

    # A separately rebuilt dashboard must fail even when both artifacts are
    # otherwise complete, reproducing the release-only regression.
    $mismatched = New-BuildFixture `
        -Name 'mismatched' `
        -GatewayBuildId 'release-build-a' `
        -ControlUiBuildId 'release-build-b'
    Assert-Fails -MessagePattern 'OpenClaw build identity mismatch' -Action {
        & $scriptPath -OpenClawDirectory $mismatched @validationParameters
    }

    $legacyId = "$expectedVersion-$($expectedCommit.Substring(0, 12))"
    $legacy = New-BuildFixture -Name 'legacy' `
        -GatewayBuildId 'unused' -ControlUiBuildId $legacyId -Legacy
    & $scriptPath -OpenClawDirectory $legacy @validationParameters
    $legacyMismatch = New-BuildFixture -Name 'legacy-mismatch' `
        -GatewayBuildId 'unused' -ControlUiBuildId "$expectedVersion-bbbbbbbbbbbb" -Legacy
    Assert-Fails -MessagePattern 'OpenClaw build identity mismatch' -Action {
        & $scriptPath -OpenClawDirectory $legacyMismatch @validationParameters
    }
    $legacyMissingUi = New-BuildFixture -Name 'legacy-missing-ui' `
        -GatewayBuildId 'unused' -OmitControlUiBuildId -Legacy
    Assert-Fails -MessagePattern 'Control UI build identity is missing' -Action {
        & $scriptPath -OpenClawDirectory $legacyMissingUi @validationParameters
    }

    foreach ($field in @('version', 'commit')) {
        $fixture = New-BuildFixture -Name "wrong-$field" `
            -GatewayBuildId 'unused' -ControlUiBuildId $legacyId -Legacy
        $path = Join-Path $fixture 'dist\build-info.json'
        $info = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $info.$field = 'unexpected'
        $info | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
        Assert-Fails -MessagePattern 'does not match the resolved OpenClaw source' -Action {
            & $scriptPath -OpenClawDirectory $fixture @validationParameters
        }
        $info.PSObject.Properties.Remove($field)
        $info | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
        Assert-Fails -MessagePattern "provenance is missing '$field'" -Action {
            & $scriptPath -OpenClawDirectory $fixture @validationParameters
        }
    }

    $manifestMismatch = New-BuildFixture -Name 'manifest-mismatch' `
        -GatewayBuildId 'unused' -ControlUiBuildId $legacyId -Legacy
    '{"name":"openclaw","version":"2026.6.6"}' |
        Set-Content -LiteralPath (Join-Path $manifestMismatch 'package.json')
    Assert-Fails -MessagePattern 'does not match the resolved OpenClaw source' -Action {
        & $scriptPath -OpenClawDirectory $manifestMismatch @validationParameters
    }

    foreach ($invalidId in @('', $null, 123)) {
        $path = Join-Path $legacy 'dist\build-info.json'
        $info = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $info | Add-Member -NotePropertyName buildId -NotePropertyValue $invalidId -Force
        $info | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
        Assert-Fails -MessagePattern 'Gateway build identity is missing or invalid' -Action {
            & $scriptPath -OpenClawDirectory $legacy @validationParameters
        }
    }
    Remove-Item -LiteralPath (Join-Path $matching 'dist\control-ui\assets\app.js')
    Assert-Fails -MessagePattern 'client bundle does not contain Gateway build identity' -Action {
        & $scriptPath -OpenClawDirectory $matching @validationParameters
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host 'OpenClaw build identity validation tests passed.'
