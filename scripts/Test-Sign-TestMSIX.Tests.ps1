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

try {
    New-Item -Path $artifactsDirectory -ItemType Directory -Force | Out-Null

    $calls = [System.Collections.Generic.List[string]]::new()
    $operations = @{
        NewCertificate = {
            param($publisher)

            $calls.Add('new')
            [pscustomobject]@{ Thumbprint = 'fake-thumbprint' }
        }
        RemoveCertificate = {
            param($certificate)

            $calls.Add('remove')
        }
    }
    $threw = $false
    try {
        & $scriptPath `
            -ArtifactsDirectory $artifactsDirectory `
            -OutputDirectory $outputDirectory `
            -Operations $operations
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notmatch 'No signable package directories were found') {
            throw
        }
    }
    if (-not $threw) {
        throw 'Signing unexpectedly succeeded without an architecture directory.'
    }
    if (Test-Path -LiteralPath $outputDirectory) {
        throw 'Signing created an output directory despite finding no architecture directory.'
    }
    if ($calls.Count -ne 0) {
        throw "Signing invoked certificate operations on the no-op path: $($calls -join ', ')."
    }

    foreach ($invalidOperations in @(
            @{ NewCertificat = { } },
            @{ NewCertificate = 'not a scriptblock' }
        )) {
        $threw = $false
        try {
            & $scriptPath `
                -ArtifactsDirectory $artifactsDirectory `
                -OutputDirectory $outputDirectory `
                -Operations $invalidOperations
        }
        catch {
            $threw = $true
            if ($_.Exception.Message -notmatch 'Unknown or invalid test signing operation adapter') {
                throw
            }
        }
        if (-not $threw) {
            throw 'Signing accepted an invalid certificate operation override.'
        }
        if (Test-Path -LiteralPath $outputDirectory) {
            throw 'Signing created an output directory before rejecting an invalid operation override.'
        }
    }

    Write-Host 'Test MSIX test-signing operation regression tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
