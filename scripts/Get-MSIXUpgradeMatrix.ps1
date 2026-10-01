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

foreach ($delivery in @(
    [pscustomobject]@{
        type = 'standalone'
        directory = 'x64'
        suffix = 'x64'
        file = 'OpenClawGateway-x64.msix'
    },
    [pscustomobject]@{
        type = 'bundle'
        directory = 'bundle'
        suffix = 'bundle'
        file = 'OpenClawGateway.msixbundle'
    }
)) {
    $include.Add([pscustomobject]@{
        name = "Store update · $($delivery.type)"
        id = "store-$($delivery.type)"
        transition_mode = 'store-in-place'
        channel = 'store'
        delivery_dir = $delivery.directory
        artifact = "openclaw-gateway-msix-store-unsigned-$($delivery.suffix)"
        candidate_file = $delivery.file
        release_tag = ''
        baseline_asset = ''
    })
}

@{ include = $include } | ConvertTo-Json -Depth 4 -Compress
