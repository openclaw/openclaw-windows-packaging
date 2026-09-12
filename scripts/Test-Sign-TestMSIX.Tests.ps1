[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Test MSIX signing tests require Windows.'
}

$scriptPath = Join-Path $PSScriptRoot 'Sign-TestMSIX.ps1'
$testRoot = Join-Path $env:TEMP (
    "openclaw-sign-testmsix-$([guid]::NewGuid().ToString('N'))"
)
$artifactsDirectory = Join-Path $testRoot 'artifacts'
$outputDirectory = Join-Path $testRoot 'signed'
$publisher = 'CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US'
$thumbprintsBefore = @(
    Get-ChildItem -Path 'Cert:\CurrentUser\My' |
        Where-Object Subject -eq $publisher |
        Select-Object -ExpandProperty Thumbprint
)

try {
    New-Item -Path $artifactsDirectory -ItemType Directory -Force | Out-Null

    $output = @(
        & pwsh -NoProfile -File $scriptPath `
            -ArtifactsDirectory $artifactsDirectory `
            -OutputDirectory $outputDirectory 2>&1
    )
    $exitCode = $LASTEXITCODE
    $message = $output -join [Environment]::NewLine

    if ($exitCode -eq 0) {
        throw 'Signing unexpectedly succeeded without an architecture directory.'
    }
    if ($message -notmatch 'No architecture directories were found') {
        throw "Signing failure did not identify the missing architecture directories. Output: $message"
    }
    if (Test-Path -LiteralPath $outputDirectory) {
        throw 'Signing created an output directory despite finding no architecture directory.'
    }

    $thumbprintsAfter = @(
        Get-ChildItem -Path 'Cert:\CurrentUser\My' |
            Where-Object Subject -eq $publisher |
            Select-Object -ExpandProperty Thumbprint
    )
    $newThumbprints = Compare-Object `
        -ReferenceObject $thumbprintsBefore `
        -DifferenceObject $thumbprintsAfter `
        -PassThru |
        Where-Object SideIndicator -eq '=>'
    if ($null -ne $newThumbprints) {
        throw 'Signing created a publisher certificate on the no-op failure path.'
    }

    Write-Host 'Test MSIX no-op signing regression test passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
