[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory,

    [Parameter(Mandatory)]
    [string]$PolicyPath,

    [Parameter(Mandatory)]
    [string]$RequestedRef,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$PackagingCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $entries = @($Archive.Entries | Where-Object {
        $_.FullName -eq $Path
    })
    if ($entries.Count -ne 1) {
        throw "Expected one '$Path' entry; found $($entries.Count)."
    }

    $stream = $entries[0].Open()
    $reader = [IO.StreamReader]::new($stream)
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-ZipEntrySha256 {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $entries = @($Archive.Entries | Where-Object {
        $_.FullName -eq $Path
    })
    if ($entries.Count -ne 1) {
        throw "Expected one '$Path' entry; found $($entries.Count)."
    }

    $stream = $entries[0].Open()
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

function Get-ZipEntryLength {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $entries = @($Archive.Entries | Where-Object {
        $_.FullName -eq $Path
    })
    if ($entries.Count -ne 1) {
        throw "Expected one '$Path' entry; found $($entries.Count)."
    }

    return $entries[0].Length
}

$resolvedArtifactsDirectory = (
    Resolve-Path -LiteralPath $ArtifactsDirectory
).Path
$resolvedPolicyPath = (Resolve-Path -LiteralPath $PolicyPath).Path
$policy = Get-Content -LiteralPath $resolvedPolicyPath -Raw |
    ConvertFrom-Json

if (
    $policy.repository -ne 'https://github.com/openclaw/openclaw' -or
    $policy.approvedCommit -notmatch '^[0-9a-fA-F]{40}$' -or
    [string]::IsNullOrWhiteSpace([string]$policy.publisher)
) {
    throw 'The Gateway MSIX release policy is invalid.'
}

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
        $metadata.payloadLayout -ne 'immutable-package' -or
        $metadata.payloadFileCount -isnot [int64] -or
        $metadata.payloadFileCount -le 0 -or
        $metadata.architecture -ne $architecture -or
        $metadata.archive -ne $msix.Name -or
        $metadata.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $metadata.signed -ne $false -or
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

    $packageArchive = [IO.Compression.ZipFile]::OpenRead($msix.FullName)
    try {
        $bundledNodeEntries = @(
            $packageArchive.Entries |
                Where-Object {
                    -not [string]::IsNullOrEmpty($_.Name) -and
                    (
                        $_.Name -ieq 'node.exe' -or
                        $_.Name -match '^node-v\d'
                    )
                }
        )
        if ($bundledNodeEntries.Count -ne 0) {
            throw "The $architecture MSIX bundles Node.js."
        }

        [xml]$manifest = Read-ZipEntryText `
            -Archive $packageArchive `
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
            -Archive $packageArchive `
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

            $actualLength = Get-ZipEntryLength `
                -Archive $packageArchive `
                -Path $packagePath
            $actualHash = Get-ZipEntrySha256 `
                -Archive $packageArchive `
                -Path $packagePath
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
            $packageArchive.Entries |
                Where-Object {
                    -not [string]::IsNullOrEmpty($_.Name) -and
                    $_.FullName.StartsWith(
                        'app/',
                        [StringComparison]::OrdinalIgnoreCase
                    )
                } |
                ForEach-Object {
                    [Uri]::UnescapeDataString($_.FullName)
                }
        )
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

Write-Host (
    "Authorized official signing for OpenClaw commit $approvedCommit " +
    "and Gateway MSIX version $expectedPackageVersion."
)
