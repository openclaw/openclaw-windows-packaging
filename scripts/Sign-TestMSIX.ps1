[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$PolicyPath = (
        Join-Path (Split-Path $PSScriptRoot -Parent) 'release-policy.json'
    ),

    [ValidateSet('store', 'sideload')]
    [string]$IdentityChannel = 'store',

    # Also embed the temporary signature in the executables this repository
    # builds before signing the packages, rehearsing the release order without
    # release credentials. A bundle directory requires both architectures.
    [switch]$SignExecutables,

    # Test seam that replaces certificate-store operations so tests never
    # touch Cert:\. Production callers omit it.
    [hashtable]$Operations = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$certificateServices = @{
    NewCertificate = {
        param($publisher)

        New-SelfSignedCertificate `
            -Type Custom `
            -Subject $publisher `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -KeyExportPolicy Exportable `
            -KeyUsage DigitalSignature `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -FriendlyName 'OpenClaw Gateway temporary MSIX test signing' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') `
            -NotAfter (Get-Date).AddDays(30)
    }
    RemoveCertificate = {
        param($certificate)

        Remove-Item `
            -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" `
            -Force `
            -ErrorAction SilentlyContinue
    }
}
foreach ($name in $Operations.Keys) {
    if (-not $certificateServices.ContainsKey($name) -or
        $Operations[$name] -isnot [scriptblock]) {
        throw "Unknown or invalid test signing operation adapter: $name"
    }
    $certificateServices[$name] = $Operations[$name]
}

if (-not $IsWindows) {
    throw 'Test MSIX signing requires Windows.'
}

$resolvedArtifactsDirectory = (
    Resolve-Path -LiteralPath $ArtifactsDirectory
).Path
$resolvedPolicyPath = (Resolve-Path -LiteralPath $PolicyPath).Path
$policy = Get-Content -LiteralPath $resolvedPolicyPath -Raw |
    ConvertFrom-Json
$publisher = if ($IdentityChannel -eq 'sideload') {
    [string]$policy.sideloadPackageIdentity.publisher
}
else {
    [string]$policy.publisher
}
if ([string]::IsNullOrWhiteSpace($publisher)) {
    throw 'The Gateway MSIX release policy publisher is missing.'
}

$architectures = @('x64', 'arm64')
$architectureDirectories = @(
    $architectures |
        ForEach-Object {
            $sourceDirectory = Join-Path $resolvedArtifactsDirectory $_
            if (Test-Path -LiteralPath $sourceDirectory -PathType Container) {
                $sourceDirectory
            }
        }
)
$bundleDirectory = Join-Path $resolvedArtifactsDirectory 'bundle'
$hasBundleDirectory = Test-Path -LiteralPath $bundleDirectory -PathType Container
if ($architectureDirectories.Count -eq 0 -and -not $hasBundleDirectory) {
    throw (
        "No signable package directories were found under " +
        "'$resolvedArtifactsDirectory'. Expected at least one of: " +
        ($architectures -join ', ') + ', bundle.'
    )
}

$signtool = Get-ChildItem `
    -LiteralPath "${env:ProgramFiles(x86)}\Windows Kits\10\bin" `
    -Filter 'signtool.exe' `
    -File `
    -Recurse |
    Where-Object {
        $_.FullName -match '\\x64\\signtool\.exe$'
    } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signtool) {
    throw 'signtool.exe was not found in the Windows SDK.'
}

New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null
$temporaryDirectory = if ($env:RUNNER_TEMP) {
    $env:RUNNER_TEMP
}
else {
    [IO.Path]::GetTempPath()
}
$temporaryPfx = Join-Path $temporaryDirectory (
    "openclaw-gateway-test-$([guid]::NewGuid().ToString('N')).pfx"
)
$passwordText = [guid]::NewGuid().ToString('N')
$executableSigningRoot = Join-Path $temporaryDirectory (
    "openclaw-gateway-test-executables-$([guid]::NewGuid().ToString('N'))"
)
$certificate = & $certificateServices.NewCertificate $publisher

try {
    [IO.File]::WriteAllBytes(
        $temporaryPfx,
        $certificate.Export(
            [Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
            $passwordText
        )
    )

    if ($SignExecutables) {
        $executableSignedArtifacts = Join-Path $executableSigningRoot 'signed'
        & (Join-Path $PSScriptRoot 'Invoke-MSIXExecutableSigning.ps1') `
            -Phase All `
            -ArtifactsDirectory $resolvedArtifactsDirectory `
            -WorkDirectory (Join-Path $executableSigningRoot 'work') `
            -SigningDirectory (Join-Path $executableSigningRoot 'signing') `
            -OutputDirectory $executableSignedArtifacts `
            -PfxPath $temporaryPfx `
            -PfxPassword $passwordText
        $resolvedArtifactsDirectory = $executableSignedArtifacts
        $bundleDirectory = Join-Path $resolvedArtifactsDirectory 'bundle'
    }

    foreach ($architecture in $architectures) {
        $sourceDirectory = Join-Path $resolvedArtifactsDirectory $architecture
        if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
            continue
        }

        $sourceMsix = @(
            Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.msix' -File
        )
        if ($sourceMsix.Count -ne 1) {
            throw (
                "Expected one unsigned $architecture MSIX in " +
                "'$sourceDirectory'; found $($sourceMsix.Count)."
            )
        }

        $sourceMetadataPath = Join-Path $sourceDirectory 'msix-metadata.json'
        if (-not (Test-Path -LiteralPath $sourceMetadataPath -PathType Leaf)) {
            throw "Missing $architecture MSIX metadata."
        }

        $destinationDirectory = Join-Path $OutputDirectory $architecture
        New-Item `
            -Path $destinationDirectory `
            -ItemType Directory `
            -Force |
            Out-Null
        $signedMsixPath = Join-Path `
            $destinationDirectory `
            $sourceMsix[0].Name
        Copy-Item `
            -LiteralPath $sourceMsix[0].FullName `
            -Destination $signedMsixPath `
            -Force

        & $signtool.FullName sign `
            /fd SHA256 `
            /f $temporaryPfx `
            /p $passwordText `
            $signedMsixPath
        if ($LASTEXITCODE -ne 0) {
            throw "Test signing failed for $architecture with exit code $LASTEXITCODE."
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $signedMsixPath
        if (
            $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint
        ) {
            throw "The $architecture test signature was not applied."
        }

        $certificatePath = Join-Path `
            $destinationDirectory `
            'OpenClawGateway-test-signing.cer'
        Export-Certificate `
            -Cert $certificate `
            -FilePath $certificatePath `
            -Type CERT |
            Out-Null

        $metadata = Get-Content -LiteralPath $sourceMetadataPath -Raw |
            ConvertFrom-Json
        $metadata.sha256 = (
            Get-FileHash -LiteralPath $signedMsixPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        $metadata.signed = $true
        $metadata | Add-Member `
            -NotePropertyName signingType `
            -NotePropertyValue test `
            -Force
        $metadata | Add-Member `
            -NotePropertyName signingCertificateThumbprint `
            -NotePropertyValue $certificate.Thumbprint `
            -Force
        $metadata |
            ConvertTo-Json |
            Set-Content `
                -LiteralPath (
                    Join-Path $destinationDirectory 'msix-metadata.json'
                ) `
                -Encoding utf8
    }

    if ($hasBundleDirectory) {
        $sourceBundles = @(
            Get-ChildItem `
                -LiteralPath $bundleDirectory `
                -Filter '*.msixbundle' `
                -File
        )
        if ($sourceBundles.Count -ne 1) {
            throw (
                "Expected one unsigned MSIX bundle in '$bundleDirectory'; " +
                "found $($sourceBundles.Count)."
            )
        }

        $destinationDirectory = Join-Path $OutputDirectory 'bundle'
        New-Item `
            -Path $destinationDirectory `
            -ItemType Directory `
            -Force |
            Out-Null
        $signedBundlePath = Join-Path `
            $destinationDirectory `
            $sourceBundles[0].Name
        Copy-Item `
            -LiteralPath $sourceBundles[0].FullName `
            -Destination $signedBundlePath `
            -Force

        & $signtool.FullName sign `
            /fd SHA256 `
            /f $temporaryPfx `
            /p $passwordText `
            $signedBundlePath
        if ($LASTEXITCODE -ne 0) {
            throw "Test signing failed for the bundle with exit code $LASTEXITCODE."
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $signedBundlePath
        if (
            $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint
        ) {
            throw 'The bundle test signature was not applied.'
        }

        $certificatePath = Join-Path `
            $destinationDirectory `
            'OpenClawGateway-test-signing.cer'
        Export-Certificate `
            -Cert $certificate `
            -FilePath $certificatePath `
            -Type CERT |
            Out-Null
    }
}
finally {
    Remove-Item -LiteralPath $temporaryPfx -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $executableSigningRoot -Recurse -Force -ErrorAction SilentlyContinue
    & $certificateServices.RemoveCertificate $certificate
}

if ($SignExecutables) {
    $signedPackages = @(
        Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse |
            Where-Object { $_.Extension -in @('.msix', '.msixbundle') } |
            ForEach-Object FullName
    )
    & (Join-Path $PSScriptRoot 'Test-MSIXExecutableSignatures.ps1') `
        -Path $signedPackages `
        -ExpectedThumbprint $certificate.Thumbprint
}

Write-Host "Created test-signed MSIX artifacts under: $OutputDirectory"
