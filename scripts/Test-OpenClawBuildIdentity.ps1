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
$serviceWorkerPath = Join-Path $controlUiDirectory 'sw.js'
$assetsDirectory = Join-Path $controlUiDirectory 'assets'

foreach ($requiredPath in @(
    $buildInfoPath
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

# The browser sends its embedded identity during the Gateway handshake. Check
# the JavaScript payload as well as the service worker before packing the app.
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

Write-Host "OpenClaw Gateway and Control UI build identity match: $gatewayBuildId"
