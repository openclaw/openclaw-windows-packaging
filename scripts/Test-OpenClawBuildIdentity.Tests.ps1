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

        [switch]$OmitControlUiBuildId,

        [switch]$OmitClientBuildId,

        [string]$DocumentScript = ''
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
    if ($OmitClientBuildId) {
        $clientBuildId = ''
    }
    "const buildInfo = { buildId: ""$clientBuildId"" };" |
        Set-Content (Join-Path $assetsDirectory 'app.js') -Encoding utf8
    [IO.File]::WriteAllText(
        (Join-Path $controlUiDirectory 'index.html'),
        "<html><head>$DocumentScript</head><body></body></html>")

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

    # Current upstream builds read document-owned identity while retaining
    # immutable JavaScript. The former bundle-only guard rejects this layout.
    $documentPrefix = '<script data-cfasync="false" id="openclaw-control-ui-build-info">'
    $assignment = 'globalThis.OPENCLAW_CONTROL_UI_BUILD_INFO='
    $validDocument = $documentPrefix + $assignment +
        '{"buildId":"release-build-a"};</script>'
    $documentOwned = New-BuildFixture `
        -Name 'document-owned' `
        -GatewayBuildId 'release-build-a' `
        -ControlUiBuildId 'release-build-a' `
        -OmitClientBuildId `
        -DocumentScript $validDocument
    & $scriptPath -OpenClawDirectory $documentOwned

    # Never accept a bad document through the historical bundle path. Every
    # rejection fixture deliberately retains a matching identity in app.js.
    $invalidDocuments = @(
        @{ Name = 'malformed-json'; Json = '{broken' }
        @{ Name = 'json-null'; Json = 'null' }
        @{ Name = 'json-array'; Json = '[]' }
        @{ Name = 'json-string'; Json = '"release-build-a"' }
        @{ Name = 'missing-build-id'; Json = '{}' }
        @{ Name = 'null-build-id'; Json = '{"buildId":null}' }
        @{ Name = 'numeric-build-id'; Json = '{"buildId":123}' }
        @{ Name = 'array-build-id'; Json = '{"buildId":[]}' }
        @{ Name = 'empty-build-id'; Json = '{"buildId":""}' }
        @{ Name = 'blank-build-id'; Json = '{"buildId":" "}' }
        @{ Name = 'mismatched-build-id'; Json = '{"buildId":"release-build-b"}' }
        @{ Name = 'case-mismatch'; Json = '{"buildId":"RELEASE-BUILD-A"}' }
        @{ Name = 'duplicate-build-id'; Json = '{"buildId":"release-build-a","buildId":"release-build-a"}' }
        @{ Name = 'commented-json'; Json = '{/* invalid */"buildId":"release-build-a"}' }
        @{ Name = 'trailing-comma'; Json = '{"buildId":"release-build-a",}' }
    )
    foreach ($case in $invalidDocuments) {
        $fixture = New-BuildFixture `
            -Name $case.Name `
            -GatewayBuildId 'release-build-a' `
            -ControlUiBuildId 'release-build-a' `
            -DocumentScript ($documentPrefix + $assignment + $case.Json + ';</script>')
        Assert-Fails -MessagePattern 'Control UI document build identity' -Action {
            & $scriptPath -OpenClawDirectory $fixture
        }
    }
    foreach ($case in @(
        @{ Name = 'duplicate-marker'; Script = $validDocument + $validDocument }
        @{ Name = 'missing-close'; Script = $validDocument.Replace('</script>', '') }
        @{ Name = 'wrong-element'; Script = $validDocument.Replace('script', 'div') }
        @{ Name = 'wrong-marker'; Script = $validDocument.Replace('openclaw-control-ui-build-info', 'wrong-marker') }
        @{ Name = 'wrong-assignment'; Script = $validDocument.Replace($assignment, 'other=') }
    )) {
        $fixture = New-BuildFixture `
            -Name $case.Name `
            -GatewayBuildId 'release-build-a' `
            -ControlUiBuildId 'release-build-a' `
            -DocumentScript $case.Script
        Assert-Fails -MessagePattern 'Control UI document build identity' -Action {
            & $scriptPath -OpenClawDirectory $fixture
        }
    }

    $missingDocument = New-BuildFixture `
        -Name 'missing-document' `
        -GatewayBuildId 'release-build-a' `
        -ControlUiBuildId 'release-build-a'
    Remove-Item (Join-Path $missingDocument 'dist/control-ui/index.html')
    Assert-Fails -MessagePattern 'build identity validation is missing' -Action {
        & $scriptPath -OpenClawDirectory $missingDocument
    }
    [IO.File]::WriteAllText((Join-Path $missingDocument 'dist/control-ui/index.html'), '')
    Assert-Fails -MessagePattern 'empty document' -Action {
        & $scriptPath -OpenClawDirectory $missingDocument
    }
    $missingLegacyIdentity = New-BuildFixture `
        -Name 'missing-legacy-client-identity' `
        -GatewayBuildId 'release-build-a' `
        -ControlUiBuildId 'release-build-a' `
        -OmitClientBuildId
    Assert-Fails -MessagePattern 'client bundle does not contain Gateway build identity' -Action {
        & $scriptPath -OpenClawDirectory $missingLegacyIdentity
    }

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
