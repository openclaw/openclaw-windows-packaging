[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Get-MSIXUpgradeMatrix.ps1'
$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "openclaw-upgrade-matrix-$([guid]::NewGuid().ToString('N'))"
$manifestPath = Join-Path $testRoot 'baselines.json'

try {
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    $baselines = @(
        for ($index = 0; $index -lt 6; $index++) {
            $deliveryType = if ($index % 2 -eq 0) {
                'standalone'
            }
            else {
                'bundle'
            }
            [pscustomobject]@{
                releaseTag = "v1.0.0.$index"
                deliveryType = $deliveryType
                assetName = "baseline-$index.$deliveryType"
                packageVersion = "1.0.0.$index"
                sha256 = ('a' * 64)
            }
        }
    )
    @{ baselines = $baselines } |
        ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8

    $matrix = & $scriptPath -ManifestPath $manifestPath |
        ConvertFrom-Json
    $scenarios = @($matrix.include)
    if ($scenarios.Count -ne 13) {
        throw "Expected 13 upgrade scenarios; received $($scenarios.Count)."
    }

    foreach ($baseline in $baselines) {
        $matches = @(
            $scenarios |
                Where-Object { $_.baseline_asset -ceq $baseline.assetName }
        )
        if (
            $matches.Count -ne 2 -or
            @($matches.transition_mode) -notcontains 'identity-reset' -or
            @($matches.transition_mode) -notcontains 'in-place'
        ) {
            throw "Baseline '$($baseline.assetName)' does not have both transition modes."
        }
    }

    $storeScenarios = @(
        $scenarios |
            Where-Object { $_.transition_mode -ceq 'store-in-place' }
    )
    if (
        $storeScenarios.Count -ne 1 -or
        [string]$storeScenarios[0].delivery_dir -cne 'bundle' -or
        [string]$storeScenarios[0].candidate_file -cne 'OpenClawGateway.msixbundle'
    ) {
        throw 'The matrix does not cover the authentic Store bundle update.'
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'MSIX upgrade matrix tests passed.'
