[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CandidatePath,

    [Parameter(Mandatory)]
    [string]$CandidateCertificatePath,

    [Parameter(Mandatory)]
    [string]$ExpectedCandidateVersion,

    [Parameter(Mandatory)]
    [string]$StoreProductId,

    [Parameter(Mandatory)]
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Microsoft Store MSIX upgrade validation requires Windows.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$policy = Get-Content `
    -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'release-policy.json') `
    -Raw |
    ConvertFrom-Json
$packageName = [string]$policy.packageIdentityName
$packageFamilyName = [string]$policy.packageFamilyName
$publisher = [string]$policy.publisher
if (
    [string]::IsNullOrWhiteSpace($packageName) -or
    [string]::IsNullOrWhiteSpace($packageFamilyName) -or
    [string]::IsNullOrWhiteSpace($publisher) -or
    [string]::IsNullOrWhiteSpace($StoreProductId)
) {
    throw 'Store upgrade validation requires complete release policy and product identity.'
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
                Version = [string]$x64Package.Version
                DeliveryType = 'bundle'
            }
        }

        $identity = $manifest.Package.Identity
        [pscustomobject]@{
            Name = [string]$identity.Name
            Publisher = [string]$identity.Publisher
            Version = [string]$identity.Version
            DeliveryType = 'standalone'
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-StorePackage {
    @(
        Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
    )
}

$testOwnsPackage = $false

function Remove-TestPackage {
    if (-not $script:testOwnsPackage) {
        return
    }
    $packages = @(Get-StorePackage)
    if ($packages.Count -gt 1) {
        throw 'More than one Store Gateway registration exists during cleanup.'
    }
    if ($packages.Count -eq 1) {
        if ([string]$packages[0].PackageFamilyName -cne $packageFamilyName) {
            throw 'Refusing to remove a Store package not owned by this test.'
        }
        Remove-AppxPackage `
            -Package $packages[0].PackageFullName `
            -ErrorAction Stop
    }
    $script:testOwnsPackage = $false
}

function Install-StoreBaseline {
    if (@(Get-StorePackage).Count -ne 0) {
        throw 'Refusing to install over a Store Gateway package not owned by this test.'
    }
    # WinGet may leave a registration even when it reports failure or the
    # registration poll times out. Arm cleanup before starting the install.
    $script:testOwnsPackage = $true
    $winget = Get-Command winget.exe -ErrorAction Stop
    $output = @(
        & $winget.Source install `
            --id $StoreProductId `
            --source msstore `
            --exact `
            --silent `
            --accept-source-agreements `
            --accept-package-agreements `
            --disable-interactivity 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        throw (
            "WinGet could not install Store product $StoreProductId " +
            "(exit $LASTEXITCODE): $($output -join [Environment]::NewLine)"
        )
    }

    $deadline = (Get-Date).AddMinutes(2)
    do {
        $packages = @(Get-StorePackage)
        if ($packages.Count -eq 1) {
            break
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    if ($packages.Count -ne 1) {
        throw 'The Microsoft Store did not create exactly one Gateway registration.'
    }
    $installed = $packages[0]
    if (
        [string]$installed.PackageFamilyName -cne $packageFamilyName -or
        [string]$installed.Publisher -cne $publisher -or
        [string]$installed.Status -cne 'Ok'
    ) {
        throw (
            'The Microsoft Store installed an unexpected Gateway package: ' +
            "$($installed.PackageFullName), publisher $($installed.Publisher), " +
            "status $($installed.Status)."
        )
    }
    $installed
}

$resolvedCandidatePath = (Resolve-Path -LiteralPath $CandidatePath).Path
$resolvedCertificatePath = (
    Resolve-Path -LiteralPath $CandidateCertificatePath
).Path
$candidateIdentity = Read-MSIXIdentity -Path $resolvedCandidatePath
if (
    $candidateIdentity.Name -cne $packageName -or
    $candidateIdentity.Publisher -cne $publisher -or
    $candidateIdentity.Version -cne $ExpectedCandidateVersion
) {
    throw "The $($candidateIdentity.DeliveryType) Store candidate identity is unexpected."
}
if (@(Get-StorePackage).Count -ne 0) {
    throw (
        'Refusing to run Store upgrade validation while the Gateway Store ' +
        'package is already installed. Use an isolated clean test account.'
    )
}

$certificate = Import-Certificate `
    -FilePath $resolvedCertificatePath `
    -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
$result = $null
try {
    if ([string]$certificate.Subject -cne $publisher) {
        throw 'The Store candidate test certificate publisher is unexpected.'
    }
    $installedBaseline = Install-StoreBaseline
    $baselineVersion = [string]$installedBaseline.Version
    if ([version]$ExpectedCandidateVersion -le [version]$baselineVersion) {
        throw (
            "The Store candidate $ExpectedCandidateVersion must be newer than " +
            "the installed Store baseline $baselineVersion."
        )
    }
    $localState = Join-Path `
        $env:LOCALAPPDATA `
        "Packages\$packageFamilyName\LocalState"
    New-Item -Path $localState -ItemType Directory -Force | Out-Null
    $markerPath = Join-Path $localState 'msix-store-upgrade-proof.txt'
    $marker = "store-upgrade-from-$baselineVersion"
    Set-Content -LiteralPath $markerPath -Value $marker -Encoding utf8

    Add-AppxPackage -Path $resolvedCandidatePath -ErrorAction Stop
    $packages = @(Get-StorePackage)
    if ($packages.Count -ne 1) {
        throw 'Windows did not retain exactly one Store Gateway registration.'
    }
    $installedCandidate = $packages[0]
    $markerWasRetained = Test-Path -LiteralPath $markerPath -PathType Leaf
    if ($markerWasRetained) {
        $markerWasRetained = (
            Get-Content -LiteralPath $markerPath -Raw
        ).Trim() -ceq $marker
    }
    if (
        [string]$installedCandidate.PackageFamilyName -cne $packageFamilyName -or
        [string]$installedCandidate.Version -cne $ExpectedCandidateVersion -or
        [string]$installedCandidate.Status -cne 'Ok' -or
        -not $markerWasRetained
    ) {
        throw (
            "The $($candidateIdentity.DeliveryType) Store update did not retain " +
            'package identity and LocalState.'
        )
    }

    $result = [pscustomobject]@{
        storeProductId = $StoreProductId
        deliveryType = $candidateIdentity.DeliveryType
        baselineVersion = [string]$installedBaseline.Version
        candidateVersion = [string]$installedCandidate.Version
        packageFamilyName = [string]$installedCandidate.PackageFamilyName
        status = [string]$installedCandidate.Status
        identityTransition = 'store-in-place'
        localStateRetained = $true
    }
}
finally {
    try {
        Remove-TestPackage
    }
    finally {
        if ($null -ne $certificate) {
            Remove-Item `
                -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }
}

$evidenceDirectory = Split-Path -Parent $EvidencePath
if (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) {
    New-Item -Path $evidenceDirectory -ItemType Directory -Force | Out-Null
}
[pscustomobject]@{
    testedAt = (Get-Date).ToUniversalTime().ToString('o')
    runner = [Environment]::OSVersion.VersionString
    storeProductId = $StoreProductId
    transition = $result
} |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $EvidencePath -Encoding utf8

Write-Host (
    'Microsoft Store in-place upgrade validation passed for the ' +
    "$($candidateIdentity.DeliveryType) candidate."
)
