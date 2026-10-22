[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaselinesPath,

    [Parameter(Mandatory)]
    [string]$BaselinesDirectory,

    [Parameter(Mandatory)]
    [string]$CandidatePath,

    [Parameter(Mandatory)]
    [string]$CandidateCertificatePath,

    [Parameter(Mandatory)]
    [string]$ExpectedCandidateVersion,

    [ValidateSet('identity-reset', 'in-place')]
    [string]$TransitionMode = 'identity-reset',

    [Parameter(Mandatory)]
    [string]$BaselineAssetName,

    [Parameter(Mandatory)]
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'MSIX upgrade validation requires Windows.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$policy = Get-Content `
    -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'release-policy.json') `
    -Raw |
    ConvertFrom-Json
$sideloadPackageName = [string]$policy.sideloadPackageIdentity.name
$sideloadPackageFamilyName = [string]$policy.sideloadPackageIdentity.familyName
$sideloadPublisher = [string]$policy.sideloadPackageIdentity.publisher
$candidatePackageName = if ($TransitionMode -ceq 'in-place') {
    $sideloadPackageName
}
else {
    [string]$policy.packageIdentityName
}
$candidatePackageFamilyName = if ($TransitionMode -ceq 'in-place') {
    $sideloadPackageFamilyName
}
else {
    [string]$policy.packageFamilyName
}
$candidatePublisher = if ($TransitionMode -ceq 'in-place') {
    $sideloadPublisher
}
else {
    [string]$policy.publisher
}
$previousPackageName = $sideloadPackageName
$previousPackageFamilyName = $sideloadPackageFamilyName
$previousPublisher = $sideloadPublisher
if (
    [string]::IsNullOrWhiteSpace($candidatePackageName) -or
    [string]::IsNullOrWhiteSpace($candidatePackageFamilyName) -or
    [string]::IsNullOrWhiteSpace($candidatePublisher) -or
    [string]::IsNullOrWhiteSpace($previousPackageName) -or
    [string]::IsNullOrWhiteSpace($previousPackageFamilyName) -or
    [string]::IsNullOrWhiteSpace($previousPublisher)
) {
    throw 'The release policy does not define the required package identities.'
}

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

$gatewayPackageNames = @(
    $candidatePackageName,
    $previousPackageName
) | Select-Object -Unique

function Get-GatewayPackages {
    @(
        foreach ($name in $gatewayPackageNames) {
            Get-AppxPackage -Name $name -ErrorAction SilentlyContinue
        }
    )
}

$testOwnsPackage = $false
$testPackageFamilyName = $null
$testPackageName = $null

function Remove-TestPackage {
    if (-not $script:testOwnsPackage) {
        return
    }

    $packages = @(
        Get-AppxPackage `
            -Name $script:testPackageName `
            -ErrorAction SilentlyContinue
    )
    if ($packages.Count -gt 1) {
        throw 'More than one owned Gateway registration exists during cleanup.'
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
    $script:testPackageName = $null
}

function Install-TestPackage {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedName,
        [Parameter(Mandatory)][string]$ExpectedFamilyName
    )

    if (@(Get-GatewayPackages).Count -ne 0) {
        throw 'Refusing to install over an OpenClaw package not owned by this test.'
    }
    # The clean-machine guard above establishes ownership before installation,
    # allowing finally cleanup even if installation only partially succeeds.
    $script:testOwnsPackage = $true
    $script:testPackageName = $ExpectedName
    Add-AppxPackage -Path $Path -ErrorAction Stop
    $packages = @(
        Get-AppxPackage -Name $ExpectedName -ErrorAction SilentlyContinue
    )
    if ($packages.Count -ne 1) {
        throw 'Windows did not create exactly one expected Gateway registration.'
    }
    $script:testPackageFamilyName = [string]$packages[0].PackageFamilyName
    if ($script:testPackageFamilyName -cne $ExpectedFamilyName) {
        throw (
            'Windows registered an unexpected package family: ' +
            $script:testPackageFamilyName
        )
    }
    $packages[0]
}

function Update-TestPackage {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedName,
        [Parameter(Mandatory)][string]$ExpectedFamilyName
    )

    if (
        -not $script:testOwnsPackage -or
        $script:testPackageName -cne $ExpectedName -or
        $script:testPackageFamilyName -cne $ExpectedFamilyName
    ) {
        throw 'Refusing to update an OpenClaw package not owned by this test.'
    }
    Add-AppxPackage -Path $Path -ErrorAction Stop
    $packages = @(
        Get-AppxPackage -Name $ExpectedName -ErrorAction SilentlyContinue
    )
    if ($packages.Count -ne 1) {
        throw 'Windows did not retain exactly one Gateway registration after update.'
    }
    if ([string]$packages[0].PackageFamilyName -cne $ExpectedFamilyName) {
        throw 'Windows changed the package family during an in-place update.'
    }
    $packages[0]
}

$resolvedBaselinesPath = (Resolve-Path -LiteralPath $BaselinesPath).Path
$resolvedBaselinesDirectory = (
    Resolve-Path -LiteralPath $BaselinesDirectory
).Path
$resolvedCandidatePath = (Resolve-Path -LiteralPath $CandidatePath).Path
$resolvedCertificatePath = (
    Resolve-Path -LiteralPath $CandidateCertificatePath
).Path
$candidateIdentity = Read-MSIXIdentity -Path $resolvedCandidatePath
if (
    $candidateIdentity.Name -cne $candidatePackageName -or
    $candidateIdentity.Architecture -cne 'x64' -or
    $candidateIdentity.Version -cne $ExpectedCandidateVersion
) {
    throw 'The candidate MSIX identity is unexpected.'
}

if ($candidateIdentity.Publisher -cne $candidatePublisher) {
    throw 'The candidate MSIX publisher does not match release policy.'
}

$baselineManifest = Get-Content -LiteralPath $resolvedBaselinesPath -Raw |
    ConvertFrom-Json
$baselines = @($baselineManifest.baselines)
if ($baselines.Count -ne 6) {
    throw 'Upgrade validation requires proof and latest-production baselines for both delivery types.'
}
$matchingBaselines = @(
    $baselines |
        Where-Object { [string]$_.assetName -ceq $BaselineAssetName }
)
if ($matchingBaselines.Count -ne 1) {
    throw "Upgrade baseline '$BaselineAssetName' must identify exactly one manifest entry."
}
$baseline = $matchingBaselines[0]
$deliveryType = [string]$baseline.deliveryType
if ($deliveryType -notin @('standalone', 'bundle')) {
    throw "Unknown delivery type '$deliveryType'."
}
if ($candidateIdentity.DeliveryType -cne $deliveryType) {
    throw (
        "The $($candidateIdentity.DeliveryType) candidate does not match " +
        "the $deliveryType baseline."
    )
}
if (@(Get-GatewayPackages).Count -ne 0) {
    throw (
        'Refusing to run MSIX identity-transition validation while a Gateway package is ' +
        'already installed. Use an isolated clean test account.'
    )
}

$certificate = Import-Certificate `
    -FilePath $resolvedCertificatePath `
    -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
$result = $null

try {
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
        $baselineIdentity.Name -cne $previousPackageName -or
        $baselineIdentity.Publisher -cne $previousPublisher -or
        $baselineIdentity.Architecture -cne 'x64' -or
        $baselineIdentity.Version -cne [string]$baseline.packageVersion -or
        $baselineIdentity.DeliveryType -cne $deliveryType
    ) {
        throw "Upgrade baseline '$($baseline.assetName)' has an unexpected identity."
    }
    if ([version]$candidateIdentity.Version -le [version]$baselineIdentity.Version) {
        throw 'The candidate MSIX must be newer than the upgrade baseline.'
    }

    $installedBaseline = Install-TestPackage `
        -Path $baselinePath `
        -ExpectedName $previousPackageName `
        -ExpectedFamilyName $previousPackageFamilyName
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

    $installedCandidate = if ($TransitionMode -ceq 'in-place') {
        Update-TestPackage `
            -Path $resolvedCandidatePath `
            -ExpectedName $candidatePackageName `
            -ExpectedFamilyName $candidatePackageFamilyName
    }
    else {
        Remove-TestPackage
        Install-TestPackage `
            -Path $resolvedCandidatePath `
            -ExpectedName $candidatePackageName `
            -ExpectedFamilyName $candidatePackageFamilyName
    }
    if (
        $null -eq $installedCandidate -or
        [string]$installedCandidate.Version -cne $candidateIdentity.Version -or
        [string]$installedCandidate.Status -cne 'Ok'
    ) {
        throw "Windows did not install the candidate after '$($baseline.assetName)'."
    }
    $candidateLocalState = Join-Path `
        $env:LOCALAPPDATA `
        "Packages\$($installedCandidate.PackageFamilyName)\LocalState"
    $retainedMarkerPath = Join-Path `
        $candidateLocalState `
        'msix-upgrade-proof.txt'
    $markerExists = Test-Path `
        -LiteralPath $retainedMarkerPath `
        -PathType Leaf
    $markerContentsMatch = $false
    if ($markerExists) {
        $markerContentsMatch = (
            Get-Content -LiteralPath $retainedMarkerPath -Raw
        ).Trim() -ceq $marker
    }
    if ($TransitionMode -ceq 'in-place') {
        if (
            $installedCandidate.PackageFamilyName -cne
                $installedBaseline.PackageFamilyName -or
            -not $markerContentsMatch
        ) {
            throw 'The in-place update did not retain package identity and LocalState.'
        }
    }
    elseif (
        $installedCandidate.PackageFamilyName -ceq
            $installedBaseline.PackageFamilyName -or
        $markerExists
    ) {
        throw 'The Partner Center identity reset did not create isolated LocalState.'
    }

    $result = [pscustomobject]@{
        baselineRelease = [string]$baseline.releaseTag
        deliveryType = $deliveryType
        baselineVersion = $baselineIdentity.Version
        candidateVersion = $candidateIdentity.Version
        packageFamilyName = [string]$installedCandidate.PackageFamilyName
        status = [string]$installedCandidate.Status
        previousPackageFamilyName = [string]$installedBaseline.PackageFamilyName
        identityTransition = $TransitionMode
        localStateRetained = ($TransitionMode -ceq 'in-place' -and $markerContentsMatch)
    }
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
    transition = $result
} |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $EvidencePath -Encoding utf8

Write-Host "MSIX $TransitionMode validation passed for '$BaselineAssetName'."
