[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaselinesPath,

    [Parameter(Mandatory)]
    [string]$BaselinesDirectory,

    [Parameter(Mandatory)]
    [string]$CandidatePackagePath,

    [Parameter(Mandatory)]
    [string]$CandidateCertificatePath,

    [Parameter(Mandatory)]
    [string]$ExpectedCandidateVersion,

    [Parameter(Mandatory)]
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'MSIX upgrade validation requires Windows.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-MSIXIdentity {
    param([Parameter(Mandatory)][string]$Path)

    $archive = [IO.Compression.ZipFile]::OpenRead(
        (Resolve-Path -LiteralPath $Path).Path
    )
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) {
            throw "MSIX '$Path' does not contain AppxManifest.xml."
        }
        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
        $identity = $manifest.Package.Identity
        [pscustomobject]@{
            Name = [string]$identity.Name
            Publisher = [string]$identity.Publisher
            Architecture = [string]$identity.ProcessorArchitecture
            Version = [string]$identity.Version
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Remove-TestPackage {
    Get-AppxPackage -Name 'OpenClaw.Gateway' -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-AppxPackage `
                -Package $_.PackageFullName `
                -ErrorAction Stop
        }
}

$resolvedBaselinesPath = (Resolve-Path -LiteralPath $BaselinesPath).Path
$resolvedBaselinesDirectory = (
    Resolve-Path -LiteralPath $BaselinesDirectory
).Path
$resolvedCandidatePath = (
    Resolve-Path -LiteralPath $CandidatePackagePath
).Path
$resolvedCertificatePath = (
    Resolve-Path -LiteralPath $CandidateCertificatePath
).Path
$candidateIdentity = Read-MSIXIdentity -Path $resolvedCandidatePath
if (
    $candidateIdentity.Name -cne 'OpenClaw.Gateway' -or
    $candidateIdentity.Architecture -cne 'x64' -or
    $candidateIdentity.Version -cne $ExpectedCandidateVersion
) {
    throw 'The candidate MSIX identity is unexpected.'
}

$policy = Get-Content `
    -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'release-policy.json') `
    -Raw |
    ConvertFrom-Json
if ($candidateIdentity.Publisher -cne [string]$policy.publisher) {
    throw 'The candidate MSIX publisher does not match release policy.'
}

$baselineManifest = Get-Content -LiteralPath $resolvedBaselinesPath -Raw |
    ConvertFrom-Json
if ($baselineManifest.baselines.Count -ne 2) {
    throw 'Upgrade validation requires exactly the two published proof releases.'
}

$certificate = Import-Certificate `
    -FilePath $resolvedCertificatePath `
    -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople'
$results = [Collections.Generic.List[object]]::new()
$freshInstall = $null

try {
    foreach ($baseline in $baselineManifest.baselines) {
        Remove-TestPackage
        $baselinePath = Join-Path `
            $resolvedBaselinesDirectory `
            ([string]$baseline.assetName)
        if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
            throw "Missing upgrade baseline '$($baseline.assetName)'."
        }
        $actualHash = (
            Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        if ($actualHash -cne [string]$baseline.sha256) {
            throw "Upgrade baseline '$($baseline.assetName)' failed hash validation."
        }

        $baselineIdentity = Read-MSIXIdentity -Path $baselinePath
        if (
            $baselineIdentity.Name -cne $candidateIdentity.Name -or
            $baselineIdentity.Publisher -cne $candidateIdentity.Publisher -or
            $baselineIdentity.Architecture -cne 'x64' -or
            $baselineIdentity.Version -cne [string]$baseline.packageVersion
        ) {
            throw "Upgrade baseline '$($baseline.assetName)' has an unexpected identity."
        }
        if (
            [version]$candidateIdentity.Version -le
            [version]$baselineIdentity.Version
        ) {
            throw 'The candidate MSIX must be newer than every upgrade baseline.'
        }

        Add-AppxPackage -Path $baselinePath -ErrorAction Stop
        $installedBaseline = Get-AppxPackage -Name $candidateIdentity.Name
        if (
            $null -eq $installedBaseline -or
            [string]$installedBaseline.Version -cne $baselineIdentity.Version -or
            [string]$installedBaseline.Status -cne 'Ok'
        ) {
            throw "Windows did not install '$($baseline.assetName)' successfully."
        }

        $localState = Join-Path `
            $env:LOCALAPPDATA `
            "Packages\$($installedBaseline.PackageFamilyName)\LocalState"
        New-Item -Path $localState -ItemType Directory -Force | Out-Null
        $markerPath = Join-Path $localState 'msix-upgrade-proof.txt'
        $marker = "upgrade-from-$($baseline.packageVersion)"
        Set-Content -LiteralPath $markerPath -Value $marker -Encoding utf8

        Add-AppxPackage `
            -Path $resolvedCandidatePath `
            -ForceApplicationShutdown `
            -ErrorAction Stop
        $installedCandidate = Get-AppxPackage -Name $candidateIdentity.Name
        if (
            $null -eq $installedCandidate -or
            [string]$installedCandidate.Version -cne $candidateIdentity.Version -or
            [string]$installedCandidate.Status -cne 'Ok'
        ) {
            throw "Windows did not upgrade from '$($baseline.assetName)'."
        }
        $candidateLocalState = Join-Path `
            $env:LOCALAPPDATA `
            "Packages\$($installedCandidate.PackageFamilyName)\LocalState"
        $retainedMarkerPath = Join-Path `
            $candidateLocalState `
            'msix-upgrade-proof.txt'
        if (
            $installedCandidate.PackageFamilyName -cne
                $installedBaseline.PackageFamilyName -or
            -not (Test-Path -LiteralPath $retainedMarkerPath -PathType Leaf) -or
            (Get-Content -LiteralPath $retainedMarkerPath -Raw).Trim() -cne $marker
        ) {
            throw "LocalState was not retained across the $($baseline.packageVersion) upgrade."
        }

        $results.Add([pscustomobject]@{
            baselineRelease = [string]$baseline.releaseTag
            baselineVersion = $baselineIdentity.Version
            candidateVersion = $candidateIdentity.Version
            packageFamilyName = [string]$installedCandidate.PackageFamilyName
            status = [string]$installedCandidate.Status
            localStateRetained = $true
        })
    }

    Remove-TestPackage
    Add-AppxPackage -Path $resolvedCandidatePath -ErrorAction Stop
    $installedFresh = Get-AppxPackage -Name $candidateIdentity.Name
    if (
        $null -eq $installedFresh -or
        [string]$installedFresh.Version -cne $candidateIdentity.Version -or
        [string]$installedFresh.Status -cne 'Ok'
    ) {
        throw 'Windows did not accept a fresh candidate installation.'
    }
    $freshInstall = [pscustomobject]@{
        candidateVersion = $candidateIdentity.Version
        packageFamilyName = [string]$installedFresh.PackageFamilyName
        status = [string]$installedFresh.Status
    }
}
finally {
    Remove-TestPackage
    if ($null -ne $certificate) {
        Remove-Item `
            -LiteralPath "Cert:\CurrentUser\TrustedPeople\$($certificate.Thumbprint)" `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

$evidenceDirectory = Split-Path -Parent $EvidencePath
if (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) {
    New-Item -Path $evidenceDirectory -ItemType Directory -Force | Out-Null
}
[pscustomobject]@{
    testedAt = (Get-Date).ToUniversalTime().ToString('o')
    runner = [Environment]::OSVersion.VersionString
    candidateVersion = $candidateIdentity.Version
    freshInstall = $freshInstall
    transitions = $results
} |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $EvidencePath -Encoding utf8

Write-Host "MSIX upgrade compatibility passed for $($results.Count) proof releases."
