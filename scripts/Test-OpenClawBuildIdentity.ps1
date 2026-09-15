[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OpenClawDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedSourceCommit,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedPackageVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Verify both upstream identity formats without treating missing provenance
# or a mismatched dashboard as a successful legacy build.
$distDirectory = Join-Path $OpenClawDirectory 'dist'
$buildInfoPath = Join-Path $distDirectory 'build-info.json'
$controlUiDirectory = Join-Path $distDirectory 'control-ui'
$serviceWorkerPath = Join-Path $controlUiDirectory 'sw.js'
$assetsDirectory = Join-Path $controlUiDirectory 'assets'
$packageManifestPath = Join-Path $OpenClawDirectory 'package.json'

foreach ($requiredPath in @(
    $packageManifestPath
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
$packageManifest = Get-Content -LiteralPath $packageManifestPath -Raw |
    ConvertFrom-Json
foreach ($field in @('version', 'commit')) {
    $property = $buildInfo.PSObject.Properties[$field]
    if ($null -eq $property -or $property.Value -isnot [string] -or
        [string]::IsNullOrWhiteSpace($property.Value)) {
        throw "Gateway build provenance is missing '$field' in '$buildInfoPath'."
    }
}
if ($buildInfo.version -cne $ExpectedPackageVersion -or
    $buildInfo.commit -ine $ExpectedSourceCommit -or
    $packageManifest.name -cne 'openclaw' -or
    $packageManifest.version -cne $ExpectedPackageVersion) {
    throw 'Gateway build provenance does not match the resolved OpenClaw source.'
}

$buildIdProperty = $buildInfo.PSObject.Properties['buildId']
if ($null -ne $buildIdProperty) {
    if ($buildIdProperty.Value -isnot [string] -or
        [string]::IsNullOrWhiteSpace($buildIdProperty.Value)) {
        throw "Gateway build identity is missing or invalid in '$buildInfoPath'."
    }
    $gatewayBuildId = $buildIdProperty.Value
}
else {
    # Older upstream Vite builds use version + 12-character Git SHA rather
    # than emitting a shared buildId in dist/build-info.json.
    $shortCommit = $ExpectedSourceCommit.ToLowerInvariant().Substring(0, 12)
    $gatewayBuildId = [regex]::Replace(
        "$ExpectedPackageVersion-$shortCommit", '[^a-zA-Z0-9._-]+', '-')
    $gatewayBuildId = $gatewayBuildId.Substring(0, [Math]::Min(96, $gatewayBuildId.Length))
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

if ($null -eq $buildIdProperty) {
    Write-Host "Legacy Control UI identity matches verified source provenance: $gatewayBuildId"
}
else {
    Write-Host "OpenClaw Gateway and Control UI build identity match: $gatewayBuildId"
}
