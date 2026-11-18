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
$originalProgramFiles = ${env:ProgramFiles(x86)}

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

    # Stop at the existing certificate boundary: exercise channel binding and
    # publisher selection without certificates, signing, or an installed SDK.
    ${env:ProgramFiles(x86)} = Join-Path $testRoot 'program-files'
    $sdkDirectory = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\fixture\x64'
    New-Item -Path $sdkDirectory -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $sdkDirectory 'signtool.exe'), '')
    New-Item -Path (Join-Path $artifactsDirectory 'x64') -ItemType Directory | Out-Null
    $policyPath = Join-Path $testRoot 'policy.json'
    [IO.File]::WriteAllText($policyPath, (@{
        publisher = 'CN=Fixture Store'
        sideloadPackageIdentity = @{ publisher = 'CN=Fixture Sideload' }
    } | ConvertTo-Json))
    $publishers = [System.Collections.Generic.List[string]]::new()
    $operations.NewCertificate = {
        param($publisher)

        $publishers.Add($publisher)
        throw 'Fixture stopped before creating a certificate.'
    }
    foreach ($channel in @('store', 'Store', 'STORE', 'sideload', 'Sideload', 'SIDELOAD')) {
        $publishers.Clear()
        try {
            & $scriptPath `
                -ArtifactsDirectory $artifactsDirectory `
                -OutputDirectory $outputDirectory `
                -PolicyPath $policyPath `
                -IdentityChannel $channel `
                -Operations $operations
            throw 'Signing unexpectedly continued past the fixture boundary.'
        }
        catch {
            if ($_.Exception.Message -cne 'Fixture stopped before creating a certificate.') {
                throw
            }
        }
        $expectedPublisher = if ($channel -ieq 'sideload') {
            'CN=Fixture Sideload'
        }
        else {
            'CN=Fixture Store'
        }
        if ($publishers.Count -ne 1 -or $publishers[0] -cne $expectedPublisher) {
            throw "Channel '$channel' did not select '$expectedPublisher': $publishers"
        }
    }

    Write-Host 'Test MSIX test-signing operation regression tests passed (six channel variants).'
}
finally {
    ${env:ProgramFiles(x86)} = $originalProgramFiles
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
