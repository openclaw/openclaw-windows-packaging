[CmdletBinding()]
param(
    [string]$ManifestPath = (
        Join-Path $PSScriptRoot 'msix-upgrade-baselines.json'
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw |
    ConvertFrom-Json
$baselines = @($manifest.baselines)
if ($baselines.Count -ne 6) {
    throw 'The MSIX upgrade matrix requires exactly six baseline assets.'
}

$seenAssets = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal
)
$include = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $baselines.Count; $index++) {
    $baseline = $baselines[$index]
    $deliveryType = [string]$baseline.deliveryType
    $releaseTag = [string]$baseline.releaseTag
    $assetName = [string]$baseline.assetName
    if (
        $deliveryType -notin @('standalone', 'bundle') -or
        [string]::IsNullOrWhiteSpace($releaseTag) -or
        [string]::IsNullOrWhiteSpace($assetName)
    ) {
        throw "Upgrade baseline $index has incomplete matrix inputs."
    }
    if (-not $seenAssets.Add($assetName)) {
        throw "Upgrade baseline asset '$assetName' is duplicated."
    }

    $deliveryDirectory = if ($deliveryType -ceq 'standalone') {
        'x64'
    }
    else {
        'bundle'
    }
    $artifactSuffix = if ($deliveryType -ceq 'standalone') {
        'x64'
    }
    else {
        'bundle'
    }
    $candidateFile = if ($deliveryType -ceq 'standalone') {
        'OpenClawGateway-x64.msix'
    }
    else {
        'OpenClawGateway.msixbundle'
    }

    foreach ($transition in @(
        [pscustomobject]@{
            label = 'identity reset'
            id = 'reset'
            mode = 'identity-reset'
            channel = 'store'
        },
        [pscustomobject]@{
            label = 'sideload update'
            id = 'sideload'
            mode = 'in-place'
            channel = 'sideload'
        }
    )) {
        $include.Add([pscustomobject]@{
            name = "$($transition.label) · $releaseTag · $deliveryType"
            id = "$($transition.id)-$index-$deliveryType"
            transition_mode = $transition.mode
            channel = $transition.channel
            delivery_dir = $deliveryDirectory
            artifact = (
                "openclaw-gateway-msix-$($transition.channel)-unsigned-" +
                $artifactSuffix
            )
            candidate_file = $candidateFile
            release_tag = $releaseTag
            baseline_asset = $assetName
        })
    }
}

# The Store installs this product from a regular bundle. Windows services that
# registration through a newer bundle, not through one extracted architecture
# package. Standalone candidates are covered by the identity-reset scenarios.
$include.Add([pscustomobject]@{
    name = 'Store update · bundle'
    id = 'store-bundle'
    transition_mode = 'store-in-place'
    channel = 'store'
    delivery_dir = 'bundle'
    artifact = 'openclaw-gateway-msix-store-unsigned-bundle'
    candidate_file = 'OpenClawGateway.msixbundle'
    release_tag = ''
    baseline_asset = ''
})

@{ include = $include } | ConvertTo-Json -Depth 4 -Compress
