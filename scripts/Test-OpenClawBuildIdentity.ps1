[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OpenClawDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A release is safe to package only when the Gateway and its same-origin
# dashboard were emitted by the same OpenClaw build lifecycle.
$distDirectory = Join-Path $OpenClawDirectory 'dist'
$buildInfoPath = Join-Path $distDirectory 'build-info.json'
$controlUiDirectory = Join-Path $distDirectory 'control-ui'
$documentPath = Join-Path $controlUiDirectory 'index.html'
$serviceWorkerPath = Join-Path $controlUiDirectory 'sw.js'
$assetsDirectory = Join-Path $controlUiDirectory 'assets'

foreach ($requiredPath in @(
    $buildInfoPath
    $documentPath
    $serviceWorkerPath
    $assetsDirectory
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "OpenClaw build identity validation is missing: $requiredPath"
    }
}

$buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw |
    ConvertFrom-Json
$gatewayBuildId = [string]$buildInfo.buildId
if ([string]::IsNullOrWhiteSpace($gatewayBuildId)) {
    throw "Gateway build identity is missing from '$buildInfoPath'."
}

# Vite writes the dashboard identity into the service worker so stale browser
# assets can retire themselves when a new Gateway build is installed.
$serviceWorker = Get-Content -LiteralPath $serviceWorkerPath -Raw
$serviceWorkerMatch = [regex]::Match(
    $serviceWorker,
    '(?m)^\s*const\s+EMBEDDED_CACHE_VERSION\s*=\s*(?<value>"[^"\r\n]+")\s*;')
if (-not $serviceWorkerMatch.Success) {
    throw "Control UI build identity is missing from '$serviceWorkerPath'."
}

$controlUiBuildId = [string](
    $serviceWorkerMatch.Groups['value'].Value |
        ConvertFrom-Json
)
if ([string]::IsNullOrWhiteSpace($controlUiBuildId)) {
    throw "Control UI build identity is empty in '$serviceWorkerPath'."
}
if (-not [string]::Equals(
        $gatewayBuildId,
        $controlUiBuildId,
        [StringComparison]::Ordinal)) {
    throw (
        "OpenClaw build identity mismatch: Gateway '$gatewayBuildId'; " +
        "Control UI '$controlUiBuildId'."
    )
}

# Current builds put identity in the document before modules run, leaving
# JavaScript assets immutable. A present but invalid marker must never fall
# through to an unrelated matching string in a client bundle.
$html = Get-Content -LiteralPath $documentPath -Raw
if ([string]::IsNullOrWhiteSpace($html)) {
    throw 'Control UI document build identity cannot be checked in an empty document.'
}
$documentMarker = 'openclaw-control-ui-build-info'
if ($html.Contains($documentMarker, [StringComparison]::Ordinal) -or
    $html.Contains('OPENCLAW_CONTROL_UI_BUILD_INFO', [StringComparison]::Ordinal)) {
    $markers = [regex]::Matches($html, [regex]::Escape($documentMarker))
    # Match the producer's executable inline tag; type/src/nomodule or duplicate
    # attributes can make matching child text inert or change its browser identity.
    $scripts = [regex]::Matches(
        $html,
        '(?s)<script data-cfasync="false" id="openclaw-control-ui-build-info">(?<body>.*?)</script>')
    if ($markers.Count -ne 1 -or $scripts.Count -ne 1) {
        throw 'Control UI document build identity requires exactly one canonical script.'
    }
    $assignment = [regex]::Match(
        $scripts[0].Groups['body'].Value,
        '(?s)^\s*globalThis\.OPENCLAW_CONTROL_UI_BUILD_INFO\s*=\s*(?<json>.*?)\s*;\s*$')
    if (-not $assignment.Success) {
        throw 'Control UI document build identity assignment is invalid.'
    }
    try {
        $document = [System.Text.Json.JsonDocument]::Parse(
            [string]$assignment.Groups['json'].Value,
            [System.Text.Json.JsonDocumentOptions]::new())
    }
    catch {
        throw 'Control UI document build identity is not valid JSON.'
    }
    try {
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw 'Control UI document build identity must be a JSON object.'
        }
        $buildIds = @($document.RootElement.EnumerateObject() |
            Where-Object { $_.Name -ceq 'buildId' })
        if ($buildIds.Count -ne 1 -or
            $buildIds[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            throw 'Control UI document build identity requires one string buildId.'
        }
        $documentBuildId = $buildIds[0].Value.GetString()
        if ([string]::IsNullOrWhiteSpace($documentBuildId)) {
            throw 'Control UI document build identity is empty.'
        }
        if (-not [string]::Equals(
                $gatewayBuildId,
                $documentBuildId,
                [StringComparison]::Ordinal)) {
            throw 'Control UI document build identity does not match the Gateway.'
        }
    }
    finally {
        $document.Dispose()
    }
}
else {
    # Explicit upstream refs and older-version rollback can select builds that
    # predate document identity. Preserve their original client-bundle check.
    $clientBundleContainsBuildId = @(
        Get-ChildItem -LiteralPath $assetsDirectory -Filter '*.js' -File -Recurse |
            Where-Object {
                (Get-Content -LiteralPath $_.FullName -Raw).Contains(
                    $gatewayBuildId,
                    [StringComparison]::Ordinal)
            }
    ).Count -gt 0
    if (-not $clientBundleContainsBuildId) {
        throw (
            "Control UI client bundle does not contain Gateway build identity " +
            "'$gatewayBuildId'."
        )
    }
}

Write-Host "OpenClaw Gateway and Control UI build identity match: $gatewayBuildId"
