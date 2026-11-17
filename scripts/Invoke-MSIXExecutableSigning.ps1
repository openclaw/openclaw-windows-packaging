[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prepare', 'Complete', 'All')]
    [string]$Phase,

    # A channel directory: x64\ and/or arm64\ each with one unsigned MSIX and
    # its msix-metadata.json, plus an optional bundle\ built from both.
    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory,

    # Unpacked package layouts and the signing manifest. Keep it apart from
    # SigningDirectory so a signer never sees any other packaged executable.
    [Parameter(Mandatory)]
    [string]$WorkDirectory,

    # Receives only the executables to sign and files.txt, the exact catalog
    # for azure/artifact-signing-action's files-catalog input.
    [Parameter(Mandatory)]
    [string]$SigningDirectory,

    [string]$OutputDirectory,

    [string]$ExpectedPublisher,

    [string]$ExpectedThumbprint,

    # Phase All signs locally with signtool: a CurrentUser\My certificate or a
    # PFX file. The Artifact Signing release path uses Prepare and Complete.
    [string]$CertificateThumbprint,

    [string]$PfxPath,

    [string]$PfxPassword,

    [string]$TimestampUrl,

    [string]$MakeAppxPath,

    [string]$SignToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MSIXExecutableSigning.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem

$manifestSchemaVersion = 1
$architectures = @('x64', 'arm64')
$footprintEntries = @('AppxBlockMap.xml', '[Content_Types].xml')
$signatureEntries = @('AppxSignature.p7x', 'AppxMetadata/CodeIntegrity.cat')

if (-not $IsWindows) {
    throw 'MSIX executable signing requires Windows.'
}

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Test-PathWithin {
    param([string]$Path, [string]$Root)

    $Path -ieq $Root -or $Path.StartsWith("$Root\", [StringComparison]::OrdinalIgnoreCase)
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-PackageEntryHashes {
    param([Parameter(Mandatory)][string]$Path)

    $hashes = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }
            $decodedPath = [Uri]::UnescapeDataString($entry.FullName)
            $stream = $entry.Open()
            try {
                $hash = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData($stream)
                ).ToLowerInvariant()
            }
            finally {
                $stream.Dispose()
            }
            if ($hashes.ContainsKey($decodedPath)) {
                throw "$Path contains a duplicate decoded path: $decodedPath"
            }
            $hashes.Add($decodedPath, $hash)
        }
    }
    finally {
        $archive.Dispose()
    }

    $hashes
}

function Get-RelativePackagePath {
    param([string]$Root, [string]$Path)

    [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Invoke-MakeAppx {
    param([string[]]$Arguments, [string]$Failure)

    $output = & $script:makeAppx @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "$Failure MakeAppx.exe exited with code $LASTEXITCODE."
    }
}

function Read-BundleIdentity {
    param([Parameter(Mandatory)][string]$Path)

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry('AppxMetadata/AppxBundleManifest.xml')
        if ($null -eq $entry) {
            throw "$Path has no bundle manifest."
        }
        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $identity = $manifest.SelectSingleNode("/*[local-name()='Bundle']/*[local-name()='Identity']")
    $packages = @(
        $manifest.SelectNodes("//*[local-name()='Packages']/*[local-name()='Package']") |
            ForEach-Object {
                '{0}|{1}|{2}|{3}' -f $_.Type, $_.Architecture, $_.Version, $_.FileName
            } |
            Sort-Object
    )
    [pscustomobject]@{
        Identity = '{0}|{1}|{2}' -f $identity.Name, $identity.Publisher, $identity.Version
        Version = [string]$identity.Version
        Packages = $packages -join ';'
    }
}

function Invoke-Prepare {
    $artifacts = Get-FullPath $ArtifactsDirectory
    if (-not (Test-Path -LiteralPath $artifacts -PathType Container)) {
        throw "Artifacts directory does not exist: $artifacts"
    }
    foreach ($directory in @($work, $signing)) {
        if (Test-Path -LiteralPath $directory) {
            throw "Refusing to reuse an existing signing work directory: $directory"
        }
    }
    if (
        (Test-PathWithin $work $signing) -or
        (Test-PathWithin $signing $work) -or
        (Test-PathWithin $work $artifacts) -or
        (Test-PathWithin $signing $artifacts)
    ) {
        throw 'The artifacts, work, and signing directories must not contain one another.'
    }

    $packages = [Collections.Generic.List[object]]::new()
    $catalog = [Collections.Generic.List[string]]::new()
    New-Item -Path $work, $signing -ItemType Directory -Force | Out-Null
    foreach ($architecture in $architectures) {
        $directory = Join-Path $artifacts $architecture
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            continue
        }

        $msix = @(Get-ChildItem -LiteralPath $directory -Filter '*.msix' -File)
        if ($msix.Count -ne 1) {
            throw "Expected one $architecture MSIX in '$directory'; found $($msix.Count)."
        }
        $metadataPath = Join-Path $directory 'msix-metadata.json'
        if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
            throw "Missing $architecture MSIX metadata: $metadataPath"
        }
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        $sourceHash = Get-Sha256 $msix[0].FullName
        if (
            $metadata.architecture -cne $architecture -or
            $metadata.archive -cne $msix[0].Name -or
            ([string]$metadata.sha256).ToLowerInvariant() -cne $sourceHash -or
            $metadata.signed -ne $false
        ) {
            throw "The $architecture MSIX does not match its unsigned metadata."
        }

        $entries = Get-PackageEntryHashes $msix[0].FullName
        foreach ($signatureEntry in $signatureEntries) {
            if ($entries.ContainsKey($signatureEntry)) {
                throw "The $architecture MSIX is already package-signed; sign executables before the package."
            }
        }

        $layout = Join-Path $work "$architecture\layout"
        Invoke-MakeAppx `
            -Arguments @('unpack', '/p', $msix[0].FullName, '/d', $layout, '/o') `
            -Failure "Unpacking the $architecture MSIX failed."

        $inventory = @{}
        foreach ($property in @('sessionHostFiles', 'gatewayRecoveryFiles')) {
            foreach ($file in @($metadata.$property | Where-Object { $null -ne $_ })) {
                $packagePath = if ($property -ceq 'sessionHostFiles') {
                    "session-host/$architecture/$($file.path)"
                }
                else {
                    [string]$file.path
                }
                $inventory[$packagePath] = ([string]$file.sha256).ToLowerInvariant()
            }
        }

        $files = [Collections.Generic.List[object]]::new()
        foreach ($entryPath in @($entries.Keys | Sort-Object)) {
            if (-not (Test-MSIXOwnedExecutable -PackagePath $entryPath -Architecture $architecture)) {
                continue
            }

            $layoutFile = Join-Path $layout $entryPath
            $bytes = [IO.File]::ReadAllBytes($layoutFile)
            $peLayout = Get-PortableExecutableSecurityLayout -Bytes $bytes
            if ($null -eq $peLayout) {
                throw "$entryPath is not a portable executable; only executables belong beside the session host."
            }
            if ($peLayout.CertificateTableOffset -ne 0 -or $peLayout.CertificateTableSize -ne 0) {
                throw "$entryPath is already Authenticode-signed in the authorized $architecture package."
            }

            $hash = Get-Sha256 $layoutFile
            if ($hash -cne $entries[$entryPath]) {
                throw "Unpacking changed $entryPath."
            }
            if ($inventory.ContainsKey($entryPath)) {
                if ($inventory[$entryPath] -cne $hash) {
                    throw "$entryPath does not match the authorized $architecture metadata."
                }
                $inventory.Remove($entryPath)
            }
            elseif ($entryPath -cne 'openclaw.exe') {
                throw "$entryPath is not in the authorized $architecture metadata."
            }

            $signingPath = Join-Path $signing "$architecture\$entryPath"
            New-Item -Path (Split-Path $signingPath -Parent) -ItemType Directory -Force | Out-Null
            Copy-Item -LiteralPath $layoutFile -Destination $signingPath
            $catalog.Add("./$architecture/$entryPath")
            $files.Add([ordered]@{ path = $entryPath; length = $bytes.Length; sha256 = $hash })
        }

        if ($inventory.Count -ne 0) {
            throw "The $architecture MSIX is missing authorized executables: $(@($inventory.Keys) -join ', ')"
        }
        foreach ($required in @('openclaw.exe', 'openclaw-gateway-recovery.exe', "session-host/$architecture/openclaw-session-host.exe")) {
            if ($null -ne $files.Where({ $_['path'] -ceq $required }, 'First')[0]) {
                continue
            }
            throw "The $architecture MSIX has no $required."
        }

        $packages.Add([ordered]@{
            architecture = $architecture
            source = $msix[0].FullName
            sourceSha256 = $sourceHash
            metadata = $metadataPath
            layout = $layout
            files = $files
        })
    }

    if ($packages.Count -eq 0) {
        throw "No x64 or arm64 MSIX directory was found under '$artifacts'."
    }

    $bundle = $null
    $bundleDirectory = Join-Path $artifacts 'bundle'
    if (Test-Path -LiteralPath $bundleDirectory -PathType Container) {
        $bundles = @(Get-ChildItem -LiteralPath $bundleDirectory -Filter '*.msixbundle' -File)
        if ($bundles.Count -ne 1) {
            throw "Expected one MSIX bundle in '$bundleDirectory'; found $($bundles.Count)."
        }
        if ($packages.Count -ne $architectures.Count) {
            throw 'Rebuilding the MSIX bundle requires both x64 and arm64 packages.'
        }
        $bundleEntries = Get-PackageEntryHashes $bundles[0].FullName
        if ($bundleEntries.ContainsKey('AppxSignature.p7x')) {
            throw 'The MSIX bundle is already signed; sign executables before the bundle.'
        }
        foreach ($package in $packages) {
            $entryName = "OpenClawGateway-$($package.architecture).msix"
            if (-not $bundleEntries.ContainsKey($entryName) -or $bundleEntries[$entryName] -cne $package.sourceSha256) {
                throw "The MSIX bundle does not contain the authorized $($package.architecture) package."
            }
        }
        $bundle = [ordered]@{
            source = $bundles[0].FullName
            sourceSha256 = Get-Sha256 $bundles[0].FullName
        }
    }

    [IO.File]::WriteAllLines(
        (Join-Path $signing 'files.txt'),
        [string[]]$catalog,
        [Text.UTF8Encoding]::new($false)
    )
    [ordered]@{
        schemaVersion = $manifestSchemaVersion
        artifacts = $artifacts
        signing = $signing
        packages = $packages
        bundle = $bundle
    } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $work 'signing-manifest.json') -Encoding utf8

    Write-Host "Prepared $($catalog.Count) executables for signing in $signing."
}

function Invoke-LocalSigning {
    $signTool = Resolve-WindowsSdkTool -Name signtool.exe -Path $SignToolPath
    $credential = if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        @('/sha1', $CertificateThumbprint, '/s', 'My')
    }
    else {
        @('/f', (Resolve-Path -LiteralPath $PfxPath).Path, '/p', $PfxPassword)
    }
    $timestamp = if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
        @()
    }
    else {
        @('/tr', $TimestampUrl, '/td', 'SHA256')
    }
    $files = @(
        [IO.File]::ReadAllLines((Join-Path $signing 'files.txt')) |
            ForEach-Object { Join-Path $signing $_.Substring(2) }
    )
    $output = & $signTool sign /fd SHA256 @credential @timestamp @files 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "signtool.exe failed to sign the package executables with exit code $LASTEXITCODE."
    }
}

function Invoke-Complete {
    $manifestPath = Join-Path $work 'signing-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "No signing manifest was prepared in $work."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (
        $manifest.schemaVersion -ne $manifestSchemaVersion -or
        $manifest.signing -ine $signing -or
        $manifest.artifacts -ine (Get-FullPath $ArtifactsDirectory)
    ) {
        throw 'The signing manifest belongs to different inputs.'
    }
    if (Test-Path -LiteralPath $output) {
        throw "Refusing to reuse an existing output directory: $output"
    }
    if (
        (Test-PathWithin $output $work) -or
        (Test-PathWithin $output $signing) -or
        (Test-PathWithin $output $manifest.artifacts)
    ) {
        throw 'The output directory must be separate from the artifacts, work, and signing directories.'
    }

    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in @($manifest.packages)) {
        foreach ($file in @($package.files)) {
            [void]$expected.Add((Join-Path $signing "$($package.architecture)\$($file.path)"))
        }
    }
    foreach ($candidate in Get-ChildItem -LiteralPath $signing -File -Recurse) {
        if ($candidate.Name -ceq 'files.txt' -and $candidate.DirectoryName -ieq $signing) {
            continue
        }
        if (-not $expected.Contains($candidate.FullName)) {
            throw "Unexpected file in the signing directory: $($candidate.FullName)"
        }
    }

    $signatureCheck = if ([string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
        @{ ExpectedPublisher = $ExpectedPublisher }
    }
    else {
        @{ ExpectedThumbprint = $ExpectedThumbprint }
    }

    $completed = @{}
    foreach ($package in @($manifest.packages)) {
        $architecture = [string]$package.architecture
        if ((Get-Sha256 $package.source) -cne $package.sourceSha256) {
            throw "The authorized $architecture MSIX changed after preparation."
        }

        $signedFiles = [Collections.Generic.List[object]]::new()
        $signedHashes = @{}
        foreach ($file in @($package.files)) {
            $name = "$architecture $($file.path)"
            $layoutFile = Join-Path $package.layout $file.path
            $signedFile = Join-Path $signing "$architecture\$($file.path)"
            $original = [IO.File]::ReadAllBytes($layoutFile)
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($original)).ToLowerInvariant() -cne $file.sha256) {
                throw "The unpacked $name changed after preparation."
            }
            $signed = [IO.File]::ReadAllBytes($signedFile)
            Assert-AuthenticodeSignatureOnlyChange -Original $original -Signed $signed -Name $name
            $signature = Assert-ExecutableSignature -Path $signedFile -Name $name @signatureCheck
            Copy-Item -LiteralPath $signedFile -Destination $layoutFile -Force

            $signedHash = Get-Sha256 $layoutFile
            $signedHashes[[string]$file.path] = $signedHash
            $signedFiles.Add([ordered]@{
                path = [string]$file.path
                length = [long]$signed.Length
                sha256 = $signedHash
                unsignedSha256 = [string]$file.sha256
                signerSubject = $signature.SignerCertificate.Subject
                signerThumbprint = $signature.SignerCertificate.Thumbprint
            })
        }

        $outputDirectory = Join-Path $output $architecture
        New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
        $repacked = Join-Path $outputDirectory (Split-Path $package.source -Leaf)
        Invoke-MakeAppx `
            -Arguments @('pack', '/d', $package.layout, '/p', $repacked) `
            -Failure "Repacking the $architecture MSIX failed."

        # The repacked package must be the authorized package plus signatures:
        # the same entries, byte-identical except the signed executables and
        # the footprint MakeAppx regenerates, and still package-unsigned.
        $sourceEntries = Get-PackageEntryHashes $package.source
        $repackedEntries = Get-PackageEntryHashes $repacked
        foreach ($entryPath in $repackedEntries.Keys) {
            if (-not $sourceEntries.ContainsKey($entryPath)) {
                throw "Repacking added $entryPath to the $architecture MSIX."
            }
        }
        foreach ($entryPath in $sourceEntries.Keys) {
            if (-not $repackedEntries.ContainsKey($entryPath)) {
                throw "Repacking dropped $entryPath from the $architecture MSIX."
            }
            if ($entryPath -in $footprintEntries) {
                continue
            }
            $expectedHash = if ($signedHashes.ContainsKey($entryPath)) {
                $signedHashes[$entryPath]
            }
            else {
                $sourceEntries[$entryPath]
            }
            if ($repackedEntries[$entryPath] -cne $expectedHash) {
                throw "Repacking changed $entryPath in the $architecture MSIX."
            }
        }

        $metadata = Get-Content -LiteralPath $package.metadata -Raw | ConvertFrom-Json
        foreach ($property in @('sessionHostFiles', 'gatewayRecoveryFiles')) {
            foreach ($file in @($metadata.$property | Where-Object { $null -ne $_ })) {
                $packagePath = if ($property -ceq 'sessionHostFiles') {
                    "session-host/$architecture/$($file.path)"
                }
                else {
                    [string]$file.path
                }
                $signedFile = @($signedFiles | Where-Object { $_['path'] -ceq $packagePath })[0]
                $file.length = $signedFile['length']
                $file.sha256 = $signedFile['sha256']
            }
        }
        $metadata.sha256 = Get-Sha256 $repacked
        $metadata | Add-Member -NotePropertyName signedExecutables -NotePropertyValue $signedFiles -Force
        $metadata | ConvertTo-Json -Depth 6 |
            Set-Content -LiteralPath (Join-Path $outputDirectory 'msix-metadata.json') -Encoding utf8

        $completed[$architecture] = [pscustomobject]@{
            Path = $repacked
            Sha256 = $metadata.sha256
            Version = [string]$metadata.packageVersion
        }
    }

    if ($null -ne $manifest.bundle) {
        if ((Get-Sha256 $manifest.bundle.source) -cne $manifest.bundle.sourceSha256) {
            throw 'The authorized MSIX bundle changed after preparation.'
        }
        if ($completed['x64'].Version -cne $completed['arm64'].Version) {
            throw 'The x64 and arm64 package versions do not match.'
        }
        # Reuse the authorized bundle's own version: MakeAppx assigns a
        # generated one when the packages are 0.0.0.0, so the package version
        # cannot reproduce that bundle identity.
        $sourceIdentity = Read-BundleIdentity $manifest.bundle.source
        $bundlePath = Join-Path $output "bundle\$(Split-Path $manifest.bundle.source -Leaf)"
        & (Join-Path $PSScriptRoot 'Build-MSIXBundle.ps1') `
            -X64Package $completed['x64'].Path `
            -Arm64Package $completed['arm64'].Path `
            -PackageVersion $sourceIdentity.Version `
            -OutputPath $bundlePath `
            -MakeAppxPath $script:makeAppx

        $bundleEntries = Get-PackageEntryHashes $bundlePath
        foreach ($architecture in $architectures) {
            $entryName = "OpenClawGateway-$architecture.msix"
            if (
                -not $bundleEntries.ContainsKey($entryName) -or
                $bundleEntries[$entryName] -cne $completed[$architecture].Sha256
            ) {
                throw "The rebuilt MSIX bundle does not contain the signed $architecture package."
            }
        }
        $rebuiltIdentity = Read-BundleIdentity $bundlePath
        if (
            $sourceIdentity.Identity -cne $rebuiltIdentity.Identity -or
            $sourceIdentity.Packages -cne $rebuiltIdentity.Packages
        ) {
            throw 'The rebuilt MSIX bundle identity differs from the authorized bundle.'
        }
    }

    $gate = @(
        Get-ChildItem -LiteralPath $output -File -Recurse |
            Where-Object { $_.Extension -in @('.msix', '.msixbundle') } |
            ForEach-Object FullName
    )
    & (Join-Path $PSScriptRoot 'Test-MSIXExecutableSignatures.ps1') -Path $gate @signatureCheck
    Write-Host "Executable-signed MSIX artifacts are ready under $output."
}

$work = Get-FullPath $WorkDirectory
$signing = Get-FullPath $SigningDirectory
$script:makeAppx = Resolve-WindowsSdkTool -Name MakeAppx.exe -Path $MakeAppxPath

if ($Phase -in @('Complete', 'All')) {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        throw "Phase $Phase requires -OutputDirectory."
    }
    $output = Get-FullPath $OutputDirectory
}
if ($Phase -ceq 'All') {
    $hasThumbprint = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
    $hasPfx = -not [string]::IsNullOrWhiteSpace($PfxPath)
    if ($hasThumbprint -eq $hasPfx) {
        throw 'Phase All requires exactly one of -CertificateThumbprint or -PfxPath.'
    }
    if ($hasPfx -and [string]::IsNullOrEmpty($PfxPassword)) {
        throw '-PfxPath requires -PfxPassword.'
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisher) -and [string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
        $ExpectedThumbprint = if ($hasThumbprint) {
            $CertificateThumbprint
        }
        else {
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                (Resolve-Path -LiteralPath $PfxPath).Path,
                $PfxPassword
            ).Thumbprint
        }
    }
}
if ($Phase -in @('Complete', 'All')) {
    $hasPublisher = -not [string]::IsNullOrWhiteSpace($ExpectedPublisher)
    $hasExpectedThumbprint = -not [string]::IsNullOrWhiteSpace($ExpectedThumbprint)
    if ($hasPublisher -eq $hasExpectedThumbprint) {
        throw "Phase $Phase requires exactly one of -ExpectedPublisher or -ExpectedThumbprint."
    }
}

switch ($Phase) {
    'Prepare' { Invoke-Prepare }
    'Complete' { Invoke-Complete }
    'All' {
        Invoke-Prepare
        Invoke-LocalSigning
        Invoke-Complete
    }
}
