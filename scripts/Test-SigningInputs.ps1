[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory,

    [Parameter(Mandatory)]
    [string]$PolicyPath,

    [Parameter(Mandatory)]
    [string]$BundlePath,

    [Parameter(Mandatory)]
    [string]$RequestedRef,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$PackagingCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-PackageEntryIndex {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive]$Archive
    )

    $entriesByPath =
        [System.Collections.Generic.Dictionary[
            string,
            System.IO.Compression.ZipArchiveEntry
        ]]::new(
            [System.StringComparer]::OrdinalIgnoreCase
        )
    foreach ($entry in $Archive.Entries) {
        if ([string]::IsNullOrEmpty($entry.Name)) {
            continue
        }

        $decodedPath = [Uri]::UnescapeDataString($entry.FullName)
        if ($entriesByPath.ContainsKey($decodedPath)) {
            throw "The MSIX contains a duplicate decoded path: $decodedPath"
        }
        $entriesByPath.Add($decodedPath, $entry)
    }

    return $entriesByPath
}

function Get-PackageEntry {
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.Dictionary[
            string,
            System.IO.Compression.ZipArchiveEntry
        ]]$EntriesByPath,

        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not $EntriesByPath.ContainsKey($Path)) {
        throw "Expected one '$Path' entry; found 0."
    }

    return $EntriesByPath[$Path]
}

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.Dictionary[
            string,
            System.IO.Compression.ZipArchiveEntry
        ]]$EntriesByPath,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $entry = Get-PackageEntry -EntriesByPath $EntriesByPath -Path $Path
    $stream = $entry.Open()
    $reader = [IO.StreamReader]::new($stream)
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-PackageEntrySha256 {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchiveEntry]$Entry
    )

    $stream = $Entry.Open()
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString(
            $sha256.ComputeHash($stream)
        ).ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

$resolvedArtifactsDirectory = (
    Resolve-Path -LiteralPath $ArtifactsDirectory
).Path
$resolvedPolicyPath = (Resolve-Path -LiteralPath $PolicyPath).Path
$policy = Get-Content -LiteralPath $resolvedPolicyPath -Raw |
    ConvertFrom-Json

if (
    $policy.repository -ne 'https://github.com/openclaw/openclaw' -or
    [string]::IsNullOrWhiteSpace([string]$policy.releaseTag) -or
    $policy.packageVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    $policy.releaseTag -ne "v$($policy.packageVersion)" -or
    [string]::IsNullOrWhiteSpace([string]$policy.payloadPackageVersion) -or
    $policy.approvedCommit -notmatch '^[0-9a-fA-F]{40}$' -or
    [string]::IsNullOrWhiteSpace([string]$policy.publisher)
) {
    throw 'The Gateway MSIX release policy is invalid.'
}

$approvedPackageVersion = & (
    Join-Path $PSScriptRoot 'Get-WorkflowPackageVersion.ps1'
) `
    -RunNumber 1 `
    -RunAttempt 1 `
    -ReleaseVersion ([string]$policy.packageVersion)
$approvedPayloadVersion = [string]$policy.payloadPackageVersion
$approvedCommit = ([string]$policy.approvedCommit).ToLowerInvariant()
$normalizedRequestedRef = $RequestedRef.Trim().ToLowerInvariant()
if (
    $normalizedRequestedRef -notmatch '^[0-9a-f]{40}$' -or
    $normalizedRequestedRef -ne $approvedCommit
) {
    throw (
        'Official signing requires the approved immutable OpenClaw commit: ' +
        $approvedCommit
    )
}

$expectedPackagingCommit = $PackagingCommit.ToLowerInvariant()
$expectedPackageVersion = $null
$expectedPackages = @{}
foreach ($architecture in @('x64', 'arm64')) {
    $directory = Join-Path $resolvedArtifactsDirectory $architecture
    $metadataPath = Join-Path $directory 'msix-metadata.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "Missing $architecture MSIX metadata: $metadataPath"
    }

    $metadata = Get-Content -LiteralPath $metadataPath -Raw |
        ConvertFrom-Json
    $msixFiles = @(Get-ChildItem -LiteralPath $directory -Filter '*.msix' -File)
    if ($msixFiles.Count -ne 1) {
        throw (
            "Expected one unsigned $architecture MSIX in '$directory'; " +
            "found $($msixFiles.Count)."
        )
    }

    $msix = $msixFiles[0]
    if (
        $metadata.packagingRepository -ne
            'https://github.com/openclaw/openclaw-windows-packaging' -or
        $metadata.packagingCommit -ine $expectedPackagingCommit -or
        $metadata.sourceTreeDirty -ne $false -or
        $metadata.payloadRepository -ne $policy.repository -or
        $metadata.payloadRequestedRef -ine $approvedCommit -or
        $metadata.payloadResolvedCommit -ine $approvedCommit -or
        $metadata.payloadPackageVersion -ne $approvedPayloadVersion -or
        $metadata.payloadLayout -ne 'immutable-package' -or
        $metadata.payloadFileCount -isnot [int64] -or
        $metadata.payloadFileCount -le 0 -or
        $metadata.architecture -ne $architecture -or
        $metadata.archive -ne $msix.Name -or
        $metadata.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $metadata.signed -ne $false -or
        $metadata.packageVersion -ne $approvedPackageVersion -or
        $metadata.publisher -ne $policy.publisher
    ) {
        throw "The $architecture MSIX metadata is not eligible for signing."
    }

    if ($null -eq $expectedPackageVersion) {
        $expectedPackageVersion = [string]$metadata.packageVersion
    }
    elseif ($metadata.packageVersion -ne $expectedPackageVersion) {
        throw 'The x64 and ARM64 package versions do not match.'
    }

    $actualMsixHash = (
        Get-FileHash -LiteralPath $msix.FullName -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($actualMsixHash -ne ([string]$metadata.sha256).ToLowerInvariant()) {
        throw "The $architecture MSIX hash does not match its metadata."
    }
    $expectedPackages[$architecture] = @{
        Name = $msix.Name
        Path = $msix.FullName
        Sha256 = $actualMsixHash
    }

    $packageArchive = [IO.Compression.ZipFile]::OpenRead($msix.FullName)
    try {
        # MSIX percent-encodes some names, so index decoded paths once.
        $entriesByPath = New-PackageEntryIndex -Archive $packageArchive
        $bundledNodeEntries = @(
            $entriesByPath.Keys |
                Where-Object {
                    [IO.Path]::GetFileName($_) -ieq 'node.exe' -or
                    [IO.Path]::GetFileName($_) -match '^node-v\d'
                }
        )
        if ($bundledNodeEntries.Count -ne 0) {
            throw "The $architecture MSIX bundles Node.js."
        }

        [xml]$manifest = Read-ZipEntryText `
            -EntriesByPath $entriesByPath `
            -Path 'AppxManifest.xml'
        $identity = $manifest.SelectSingleNode(
            "/*[local-name()='Package']/*[local-name()='Identity']"
        )
        if (
            $null -eq $identity -or
            $identity.Publisher -ne $policy.publisher -or
            $identity.ProcessorArchitecture -ne $architecture -or
            $identity.Version -ne $metadata.packageVersion
        ) {
            throw "The $architecture MSIX manifest identity is unexpected."
        }

        $payloadFiles = Read-ZipEntryText `
            -EntriesByPath $entriesByPath `
            -Path 'payload/payload-files.json' |
            ConvertFrom-Json
        if (
            $null -eq $payloadFiles.files -or
            @($payloadFiles.files).Count -ne $metadata.payloadFileCount
        ) {
            throw "The embedded $architecture payload inventory is invalid."
        }

        $expectedApplicationPaths =
            [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::OrdinalIgnoreCase
            )
        $hasEntryPoint = $false
        foreach ($file in @($payloadFiles.files)) {
            $relativePath = [string]$file.path
            $segments = @($relativePath.Split('/'))
            # Reject rooted/absolute paths and '.'/'..' segments: the
            # inventory is untrusted signing input, and an unvalidated path
            # here would let a malicious entry escape app/ when resolved.
            if (
                [string]::IsNullOrWhiteSpace($relativePath) -or
                $relativePath.StartsWith('/') -or
                [IO.Path]::IsPathRooted($relativePath) -or
                $relativePath.Contains('\') -or
                $relativePath.Contains(':') -or
                $segments -contains '' -or
                $segments -contains '.' -or
                $segments -contains '..' -or
                $file.length -isnot [int64] -or
                $file.length -lt 0 -or
                $file.sha256 -notmatch '^[0-9a-fA-F]{64}$'
            ) {
                throw "The embedded $architecture payload inventory is invalid."
            }

            $packagePath = "app/$relativePath"
            if (-not $expectedApplicationPaths.Add($packagePath)) {
                throw (
                    "The embedded $architecture payload inventory has " +
                    'duplicate paths.'
                )
            }
            if ($relativePath -ieq 'openclaw.mjs') {
                $hasEntryPoint = $true
            }

            $entry = Get-PackageEntry `
                -EntriesByPath $entriesByPath `
                -Path $packagePath
            $actualLength = $entry.Length
            $actualHash = Get-PackageEntrySha256 -Entry $entry
            if (
                $actualLength -ne $file.length -or
                $actualHash -ine $file.sha256
            ) {
                throw (
                    "The embedded $architecture application file is invalid: " +
                    $relativePath
                )
            }
        }

        if (-not $hasEntryPoint) {
            throw (
                "The embedded $architecture payload inventory has no " +
                'openclaw.mjs.'
            )
        }

        $actualApplicationPaths = @(
            $entriesByPath.Keys |
                Where-Object {
                    $_.StartsWith(
                        'app/',
                        [StringComparison]::OrdinalIgnoreCase
                    )
                }
        )
        # The per-file loop above already verified every inventoried file's
        # hash matches the package; this set-equality check additionally
        # catches extra app/ files present in the MSIX but absent from the
        # inventory, which would otherwise go unverified.
        if (
            $actualApplicationPaths.Count -ne
                $expectedApplicationPaths.Count -or
            @($actualApplicationPaths | Where-Object {
                -not $expectedApplicationPaths.Contains($_)
            }).Count -ne 0
        ) {
            throw (
                "The embedded $architecture application file set is invalid."
            )
        }
    }
    finally {
        $packageArchive.Dispose()
    }
}

$resolvedBundlePath = (Resolve-Path -LiteralPath $BundlePath).Path
if ([IO.Path]::GetExtension($resolvedBundlePath) -ine '.msixbundle') {
    throw 'The official signing bundle must use the .msixbundle extension.'
}

$bundleArchive = [IO.Compression.ZipFile]::OpenRead($resolvedBundlePath)
try {
    $bundleEntries = New-PackageEntryIndex -Archive $bundleArchive
    [xml]$bundleManifest = Read-ZipEntryText `
        -EntriesByPath $bundleEntries `
        -Path 'AppxMetadata/AppxBundleManifest.xml'
    $bundleIdentity = $bundleManifest.SelectSingleNode(
        "/*[local-name()='Bundle']/*[local-name()='Identity']"
    )
    if (
        $null -eq $bundleIdentity -or
        $bundleIdentity.Name -ne 'OpenClaw.Gateway' -or
        $bundleIdentity.Publisher -ne $policy.publisher -or
        $bundleIdentity.Version -ne $expectedPackageVersion
    ) {
        throw 'The MSIX bundle manifest identity is unexpected.'
    }

    $bundlePackages = @(
        $bundleManifest.SelectNodes(
            "/*[local-name()='Bundle']/*[local-name()='Packages']/*[local-name()='Package']"
        )
    )
    if ($bundlePackages.Count -ne 2) {
        throw 'The MSIX bundle must contain exactly two application packages.'
    }

    $seenArchitectures =
        [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase
        )
    foreach ($bundlePackage in $bundlePackages) {
        $architecture = [string]$bundlePackage.Architecture
        if (
            -not $expectedPackages.ContainsKey($architecture) -or
            -not $seenArchitectures.Add($architecture) -or
            $bundlePackage.Type -ne 'application' -or
            $bundlePackage.Version -ne $expectedPackageVersion
        ) {
            throw 'The MSIX bundle package manifest is unexpected.'
        }

        $expectedPackage = $expectedPackages[$architecture]
        $fileName = [string]$bundlePackage.FileName
        if ($fileName -ne $expectedPackage.Name) {
            throw "The bundled $architecture MSIX filename is unexpected."
        }

        $bundleEntry = Get-PackageEntry `
            -EntriesByPath $bundleEntries `
            -Path $fileName
        $bundledHash = Get-PackageEntrySha256 -Entry $bundleEntry
        if ($bundledHash -ne $expectedPackage.Sha256) {
            throw (
                "The bundled $architecture MSIX does not match the " +
                'authorized standalone package.'
            )
        }
    }

    $embeddedMsixEntries = @(
        $bundleEntries.Keys |
            Where-Object { [IO.Path]::GetExtension($_) -ieq '.msix' }
    )
    if ($embeddedMsixEntries.Count -ne 2) {
        throw 'The MSIX bundle contains an unexpected package file set.'
    }
}
finally {
    $bundleArchive.Dispose()
}

Write-Host (
    "Authorized official signing for OpenClaw commit $approvedCommit " +
    "and Gateway MSIX version $expectedPackageVersion."
)
