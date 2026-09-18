[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OpenClawDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The Gateway and its same-origin dashboard must carry the same upstream
# build identity, including the version/commit identity used by older builds.
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
$buildIdProperty = $buildInfo.PSObject.Properties['buildId']
if ($null -ne $buildIdProperty) {
    $gatewayBuildId = [string]$buildIdProperty.Value
}
else {
    $versionProperty = $buildInfo.PSObject.Properties['version']
    $commitProperty = $buildInfo.PSObject.Properties['commit']
    if (
        $null -eq $versionProperty -or
        $versionProperty.Value -isnot [string] -or
        $versionProperty.Value -cnotmatch
            '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -or
        $null -eq $commitProperty -or
        $commitProperty.Value -isnot [string] -or
        $commitProperty.Value -cnotmatch '^[0-9a-f]{40}$'
    ) {
        throw (
            "Gateway build identity is missing from '$buildInfoPath'. " +
            'Legacy metadata requires a version and a full commit SHA.'
        )
    }

    # Older upstream Vite builds normalize <version>-<git rev-parse --short=12 HEAD>.
    $gatewayBuildId = (
        "$($versionProperty.Value)-$($commitProperty.Value.Substring(0, 12))" `
            -creplace '[^a-zA-Z0-9._-]+', '-'
    )
    $gatewayBuildId = $gatewayBuildId.Substring(
        0,
        [Math]::Min(96, $gatewayBuildId.Length))
}
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
            ([IO.File]::ReadAllText($_.FullName)).Contains(
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
