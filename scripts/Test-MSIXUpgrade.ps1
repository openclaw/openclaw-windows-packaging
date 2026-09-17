[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaselinesPath,

    [Parameter(Mandatory)]
    [string]$BaselinesDirectory,

    [Parameter(Mandatory)]
    [string]$CandidatePackagePath,

    [Parameter(Mandatory)]
    [string]$CandidateBundlePath,

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
        $isBundle = [IO.Path]::GetExtension($Path) -ieq '.msixbundle'
        $manifestPath = if ($isBundle) {
            'AppxMetadata/AppxBundleManifest.xml'
        }
        else {
            'AppxManifest.xml'
        }
        $entry = $archive.GetEntry($manifestPath)
        if ($null -eq $entry) {
            throw "Package '$Path' does not contain $manifestPath."
        }
        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
        if ($isBundle) {
            $identity = $manifest.Bundle.Identity
            $x64Package = @($manifest.Bundle.Packages.Package) |
                Where-Object { [string]$_.Architecture -ceq 'x64' } |
                Select-Object -First 1
            if ($null -eq $x64Package) {
                throw "MSIX bundle '$Path' does not contain an x64 package."
            }
            return [pscustomobject]@{
                Name = [string]$identity.Name
                Publisher = [string]$identity.Publisher
                Architecture = 'x64'
                Version = [string]$x64Package.Version
                BundleVersion = [string]$identity.Version
                DeliveryType = 'bundle'
            }
        }

        $identity = $manifest.Package.Identity
        [pscustomobject]@{
            Name = [string]$identity.Name
            Publisher = [string]$identity.Publisher
            Architecture = [string]$identity.ProcessorArchitecture
            Version = [string]$identity.Version
            BundleVersion = $null
            DeliveryType = 'standalone'
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-GatewayPackages {
    @(Get-AppxPackage -Name 'OpenClaw.Gateway' -ErrorAction SilentlyContinue)
}

$testOwnsPackage = $false
$testPackageFamilyName = $null

function Remove-TestPackage {
    if (-not $script:testOwnsPackage) {
        return
    }

    $packages = @(Get-GatewayPackages)
    if ($packages.Count -gt 1) {
        throw 'More than one OpenClaw.Gateway registration exists during cleanup.'
    }
    if ($packages.Count -eq 1) {
        if (
            $null -ne $script:testPackageFamilyName -and
            [string]$packages[0].PackageFamilyName -cne
                $script:testPackageFamilyName
        ) {
            throw 'Refusing to remove an OpenClaw package not owned by this test.'
        }
        Remove-AppxPackage `
            -Package $packages[0].PackageFullName `
            -ErrorAction Stop
    }

    $script:testOwnsPackage = $false
    $script:testPackageFamilyName = $null
}

function Install-TestPackage {
    param([Parameter(Mandatory)][string]$Path)

    if (@(Get-GatewayPackages).Count -ne 0) {
        throw 'Refusing to install over an OpenClaw package not owned by this test.'
    }
    # The clean-machine guard above establishes ownership before installation,
    # allowing finally cleanup even if installation only partially succeeds.
    $script:testOwnsPackage = $true
    Add-AppxPackage -Path $Path -ErrorAction Stop
    $packages = @(Get-GatewayPackages)
    if ($packages.Count -ne 1) {
        throw 'Windows did not create exactly one OpenClaw.Gateway registration.'
    }
    $script:testPackageFamilyName = [string]$packages[0].PackageFamilyName
    $packages[0]
}

$resolvedBaselinesPath = (Resolve-Path -LiteralPath $BaselinesPath).Path
$resolvedBaselinesDirectory = (
    Resolve-Path -LiteralPath $BaselinesDirectory
).Path
$resolvedCandidatePath = (
    Resolve-Path -LiteralPath $CandidatePackagePath
).Path
$resolvedCandidateBundlePath = (
    Resolve-Path -LiteralPath $CandidateBundlePath
).Path
$resolvedCertificatePath = (
    Resolve-Path -LiteralPath $CandidateCertificatePath
).Path
$candidateIdentity = Read-MSIXIdentity -Path $resolvedCandidatePath
$candidateBundleIdentity = Read-MSIXIdentity -Path $resolvedCandidateBundlePath
if (
    $candidateIdentity.Name -cne 'OpenClaw.Gateway' -or
    $candidateIdentity.Architecture -cne 'x64' -or
    $candidateIdentity.Version -cne $ExpectedCandidateVersion
) {
    throw 'The candidate MSIX identity is unexpected.'
}
if (
    $candidateBundleIdentity.Name -cne $candidateIdentity.Name -or
    $candidateBundleIdentity.Publisher -cne $candidateIdentity.Publisher -or
    $candidateBundleIdentity.Architecture -cne 'x64' -or
    $candidateBundleIdentity.Version -cne $ExpectedCandidateVersion -or
    $candidateBundleIdentity.DeliveryType -cne 'bundle'
) {
    throw 'The candidate MSIX bundle identity is unexpected.'
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
if ($baselineManifest.baselines.Count -ne 4) {
    throw 'Upgrade validation requires standalone and bundle proof-release baselines.'
}
if (@(Get-GatewayPackages).Count -ne 0) {
    throw (
        'Refusing to run MSIX upgrade validation while OpenClaw.Gateway is ' +
        'already installed. Use an isolated clean test account.'
    )
}

$certificate = Import-Certificate `
    -FilePath $resolvedCertificatePath `
    -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
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

        $deliveryType = [string]$baseline.deliveryType
        if ($deliveryType -notin @('standalone', 'bundle')) {
            throw "Unknown delivery type '$deliveryType'."
        }
        $baselineIdentity = Read-MSIXIdentity -Path $baselinePath
        if (
            $baselineIdentity.Name -cne $candidateIdentity.Name -or
            $baselineIdentity.Publisher -cne $candidateIdentity.Publisher -or
            $baselineIdentity.Architecture -cne 'x64' -or
            $baselineIdentity.Version -cne [string]$baseline.packageVersion -or
            $baselineIdentity.DeliveryType -cne $deliveryType
        ) {
            throw "Upgrade baseline '$($baseline.assetName)' has an unexpected identity."
        }
        if (
            [version]$candidateIdentity.Version -le
            [version]$baselineIdentity.Version
        ) {
            throw 'The candidate MSIX must be newer than every upgrade baseline.'
        }

        $installedBaseline = Install-TestPackage -Path $baselinePath
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

        $candidatePath = if ($deliveryType -ceq 'bundle') {
            $resolvedCandidateBundlePath
        }
        else {
            $resolvedCandidatePath
        }
        Add-AppxPackage `
            -Path $candidatePath `
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
            deliveryType = $deliveryType
            baselineVersion = $baselineIdentity.Version
            candidateVersion = $candidateIdentity.Version
            packageFamilyName = [string]$installedCandidate.PackageFamilyName
            status = [string]$installedCandidate.Status
            localStateRetained = $true
        })
    }

    $freshInstalls = [Collections.Generic.List[object]]::new()
    foreach ($candidate in @(
        [pscustomobject]@{
            deliveryType = 'standalone'
            path = $resolvedCandidatePath
        },
        [pscustomobject]@{
            deliveryType = 'bundle'
            path = $resolvedCandidateBundlePath
        }
    )) {
        Remove-TestPackage
        $installedFresh = Install-TestPackage -Path $candidate.path
        if (
            $null -eq $installedFresh -or
            [string]$installedFresh.Version -cne $candidateIdentity.Version -or
            [string]$installedFresh.Status -cne 'Ok'
        ) {
            throw "Windows did not accept a fresh $($candidate.deliveryType) installation."
        }
        $freshInstalls.Add([pscustomobject]@{
            deliveryType = $candidate.deliveryType
            candidateVersion = $candidateIdentity.Version
            packageFamilyName = [string]$installedFresh.PackageFamilyName
            status = [string]$installedFresh.Status
        })
    }
    $freshInstall = $freshInstalls
}
finally {
    Remove-TestPackage
    if ($null -ne $certificate) {
        Remove-Item `
            -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" `
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

Write-Host "MSIX upgrade compatibility passed for $($results.Count) proof-release paths."
