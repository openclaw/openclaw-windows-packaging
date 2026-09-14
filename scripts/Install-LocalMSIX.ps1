[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    [string]$PackageVersion,

    [string]$PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Local MSIX installation requires Windows.'
}
if ($PackageDirectory -and $PackageVersion) {
    throw 'Specify either -PackageDirectory or -PackageVersion, not both.'
}

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$localRoot = Join-Path $repositoryRoot "artifacts\local-msix\$Architecture"

function Resolve-PackageDirectory {
    if ($PackageDirectory) {
        return (Resolve-Path -LiteralPath $PackageDirectory).Path
    }

    if ($PackageVersion) {
        $versionDirectory = Join-Path $localRoot $PackageVersion
        if (-not (Test-Path -LiteralPath $versionDirectory -PathType Container)) {
            throw "The local MSIX directory was not found: $versionDirectory"
        }

        return (Resolve-Path -LiteralPath $versionDirectory).Path
    }

    if (-not (Test-Path -LiteralPath $localRoot -PathType Container)) {
        throw (
            "No local MSIX directory exists for ${Architecture}: $localRoot. " +
            'Build one with scripts\Build-LocalMSIX.ps1 first.'
        )
    }

    $candidates = @(
        Get-ChildItem -LiteralPath $localRoot -Directory |
            Where-Object {
                (Test-Path -LiteralPath (
                    Join-Path $_.FullName "OpenClawGateway-$Architecture.msix"
                ) -PathType Leaf) -and
                (Test-Path -LiteralPath (
                    Join-Path $_.FullName 'OpenClawGateway-test-signing.cer'
                ) -PathType Leaf)
            } |
            Sort-Object LastWriteTimeUtc -Descending
    )
    if ($candidates.Count -eq 0) {
        throw (
            "No signed local $Architecture MSIX was found under '$localRoot'. " +
            'Build one with scripts\Build-LocalMSIX.ps1 first.'
        )
    }

    return $candidates[0].FullName
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-ProcessArgument {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Restart-Elevated {
    $hostProcess = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($hostProcess)) {
        throw 'Unable to determine the PowerShell executable for elevation.'
    }

    $arguments = @(
        '-NoProfile'
        '-File'
        (Quote-ProcessArgument $PSCommandPath)
        '-Architecture'
        (Quote-ProcessArgument $Architecture)
    )
    if ($PackageVersion) {
        $arguments += @(
            '-PackageVersion'
            (Quote-ProcessArgument $PackageVersion)
        )
    }
    elseif ($PackageDirectory) {
        $arguments += @(
            '-PackageDirectory'
            (Quote-ProcessArgument $resolvedPackageDirectory)
        )
    }
    if ($WhatIfPreference) {
        $arguments += '-WhatIf'
    }

    Write-Host 'Requesting elevation to trust the certificate for MSIX deployment.'
    $process = Start-Process `
        -FilePath $hostProcess `
        -Verb RunAs `
        -ArgumentList $arguments `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Elevated local MSIX installation failed with exit code $($process.ExitCode)."
    }
}

$resolvedPackageDirectory = Resolve-PackageDirectory
$msixPath = Join-Path `
    $resolvedPackageDirectory `
    "OpenClawGateway-$Architecture.msix"
$certificatePath = Join-Path `
    $resolvedPackageDirectory `
    'OpenClawGateway-test-signing.cer'
$metadataPath = Join-Path $resolvedPackageDirectory 'msix-metadata.json'

foreach ($requiredPath in @($msixPath, $certificatePath, $metadataPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "The local MSIX output is missing '$requiredPath'."
    }
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw |
    ConvertFrom-Json
$certificate = Get-PfxCertificate -FilePath $certificatePath
if ($null -eq $certificate) {
    throw "The local signing certificate could not be read: $certificatePath"
}

if (
    $metadata.signed -ne $true -or
    $metadata.signingType -ne 'test' -or
    $metadata.architecture -ne $Architecture -or
    $metadata.archive -ne (Split-Path $msixPath -Leaf) -or
    $metadata.signingCertificateThumbprint -ine $certificate.Thumbprint
) {
    throw (
        "The local MSIX metadata does not match its test certificate or " +
        "architecture: $metadataPath"
    )
}

$signature = Get-AuthenticodeSignature -LiteralPath $msixPath
if ($null -eq $signature.SignerCertificate) {
    throw "The local MSIX is not signed: $msixPath"
}
if ($signature.SignerCertificate.Thumbprint -ine $certificate.Thumbprint) {
    throw (
        'The MSIX signer does not match the emitted test certificate. ' +
        "Package: $($signature.SignerCertificate.Thumbprint); " +
        "certificate: $($certificate.Thumbprint)."
    )
}

$policyPath = Join-Path $repositoryRoot 'release-policy.json'
$expectedPublisher = [string](
    (Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json).publisher
)
if ($certificate.Subject -ne $expectedPublisher) {
    throw (
        "The test certificate publisher '$($certificate.Subject)' does not " +
        "match the repository publisher '$expectedPublisher'."
    )
}

$trustedCertificatePath = "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
if (-not (Test-Path -LiteralPath $trustedCertificatePath)) {
    if (-not (Test-IsAdministrator)) {
        if ($WhatIfPreference) {
            Write-Host (
                'WhatIf: would import the certificate into ' +
                'Cert:\LocalMachine\TrustedPeople.'
            )
        }
        else {
            Restart-Elevated
            exit 0
        }
    }

    if ($PSCmdlet.ShouldProcess(
            $certificatePath,
            'Import into Cert:\LocalMachine\TrustedPeople')) {
        Import-Certificate `
            -FilePath $certificatePath `
            -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' |
            Out-Null
        Write-Host 'Imported the local test certificate into LocalMachine TrustedPeople.'
    }
}
else {
    Write-Host 'The test certificate is already trusted at machine scope.'
}

if ($PSCmdlet.ShouldProcess($msixPath, 'Install with Add-AppxPackage')) {
    Add-AppxPackage `
        -Path $msixPath `
        -ForceApplicationShutdown `
        -ForceUpdateFromAnyVersion
    Write-Host "Installed local MSIX: $msixPath"
}
else {
    Write-Host "WhatIf: would install local MSIX: $msixPath"
}
