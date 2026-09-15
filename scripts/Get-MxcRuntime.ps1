#Requires -Version 7.0
<#
.SYNOPSIS
    Stages the pinned MXC native runtime for packaging.

.DESCRIPTION
    Acquires the exact npm archive named in mxc-runtime.lock.json, verifies its
    registry integrity hash, extracts only the allowlisted runtime and licence
    entries, and verifies each staged file's length, SHA-256, and PE machine
    type before it is written to the output directory.

    The archive is treated as untrusted input until it is verified. This script
    never runs 'npm install', never runs package lifecycle scripts, and never
    executes any downloaded binary.

.PARAMETER Architecture
    Runtime architecture to stage. x64 and arm64 are staged separately.

.PARAMETER ArchivePath
    Optional path to an already-downloaded archive, for offline iteration. It
    is subject to the same integrity, length, hash, and architecture checks as
    a freshly downloaded archive.

.PARAMETER OutputDirectory
    Directory to stage into. Defaults to content\mxc\<architecture>.

.PARAMETER CacheDirectory
    Directory holding verified archives. Defaults to artifacts\mxc-cache.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$ArchivePath,

    [string]$OutputDirectory,

    [string]$CacheDirectory,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$lockPath = Join-Path $repositoryRoot 'mxc-runtime.lock.json'

function Get-Sha256Hex {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-NpmIntegrity {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha512 = [Security.Cryptography.SHA512]::Create()
        try {
            'sha512-' + [Convert]::ToBase64String($sha512.ComputeHash($stream))
        }
        finally {
            $sha512.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-PeMachine {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw "'$Path' is not a PE image."
            }

            $stream.Position = 0x3C
            $stream.Position = $reader.ReadUInt32()
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "'$Path' has no PE signature."
            }

            '0x{0:x4}' -f $reader.ReadUInt16()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Resolve-VerifiedArchive {
    param(
        [Parameter(Mandatory)]
        [psobject]$Lock,

        [string]$SuppliedPath,

        [Parameter(Mandatory)]
        [string]$CachePath
    )

    if ($SuppliedPath) {
        if (-not (Test-Path -LiteralPath $SuppliedPath -PathType Leaf)) {
            throw "The supplied MXC archive '$SuppliedPath' does not exist."
        }

        $suppliedIntegrity = Get-NpmIntegrity -Path $SuppliedPath
        if ($suppliedIntegrity -ne $Lock.tarballIntegrity) {
            throw (
                "The supplied MXC archive does not match the pinned " +
                "integrity value. Expected '$($Lock.tarballIntegrity)'; " +
                "computed '$suppliedIntegrity'."
            )
        }

        Write-Host "Using the verified local MXC archive '$SuppliedPath'."
        return $SuppliedPath
    }

    if (Test-Path -LiteralPath $CachePath -PathType Leaf) {
        $cachedIntegrity = Get-NpmIntegrity -Path $CachePath
        if ($cachedIntegrity -eq $Lock.tarballIntegrity) {
            Write-Host "Using the verified cached MXC archive '$CachePath'."
            return $CachePath
        }

        Write-Warning (
            "Discarding the cached MXC archive '$CachePath': it no longer " +
            'matches the pinned integrity value.'
        )
        Remove-Item -LiteralPath $CachePath -Force
    }

    New-Item -Path (Split-Path $CachePath -Parent) -ItemType Directory -Force |
        Out-Null
    Write-Host "Downloading $($Lock.tarballUrl)."
    $downloadPath = "$CachePath.$([guid]::NewGuid().ToString('N')).partial"
    try {
        Invoke-WebRequest `
            -Uri $Lock.tarballUrl `
            -OutFile $downloadPath `
            -MaximumRedirection 5 `
            -UseBasicParsing

        $downloadedIntegrity = Get-NpmIntegrity -Path $downloadPath
        if ($downloadedIntegrity -ne $Lock.tarballIntegrity) {
            throw (
                "The downloaded MXC archive does not match the pinned " +
                "integrity value. Expected '$($Lock.tarballIntegrity)'; " +
                "computed '$downloadedIntegrity'."
            )
        }

        Move-Item -LiteralPath $downloadPath -Destination $CachePath -Force
        return $CachePath
    }
    finally {
        if (Test-Path -LiteralPath $downloadPath -PathType Leaf) {
            Remove-Item -LiteralPath $downloadPath -Force
        }
    }
}

if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw "Missing the pinned runtime lock file '$lockPath'."
}

$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$architectureLock = $lock.architectures.$Architecture
if (-not $architectureLock) {
    throw "mxc-runtime.lock.json does not pin a runtime for '$Architecture'."
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot "content\mxc\$Architecture"
}
if (-not $CacheDirectory) {
    $CacheDirectory = Join-Path $repositoryRoot 'artifacts\mxc-cache'
}

$provenancePath = Join-Path $OutputDirectory 'mxc-runtime.json'
$stagedEntries = @($architectureLock.files) + @($lock.licenseFiles)
$expectedStagedPaths = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)
foreach ($entry in $stagedEntries) {
    [void]$expectedStagedPaths.Add(
        $entry.stagedPath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    )
}
[void]$expectedStagedPaths.Add('mxc-runtime.json')

# Re-verify rather than trusting the directory's existence: a stale or
# tampered staging directory must not silently become package content.
if (-not $Force -and (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
    $upToDate = $true
    foreach ($entry in $stagedEntries) {
        $candidate = Join-Path $OutputDirectory $entry.stagedPath
        if (
            -not (Test-Path -LiteralPath $candidate -PathType Leaf) -or
            (Get-Item -LiteralPath $candidate).Length -ne $entry.length -or
            (Get-Sha256Hex -Path $candidate) -ne $entry.sha256
        ) {
            $upToDate = $false
            break
        }
    }
    if ($upToDate) {
        foreach ($candidate in @(
            Get-ChildItem -LiteralPath $OutputDirectory -File -Force -Recurse
        )) {
            $relativePath = [IO.Path]::GetRelativePath(
                $OutputDirectory,
                $candidate.FullName
            )
            if (-not $expectedStagedPaths.Contains($relativePath)) {
                $upToDate = $false
                break
            }
        }
    }

    if ($upToDate) {
        Write-Host (
            "MXC runtime $($lock.version) ($Architecture) is already staged " +
            "in '$OutputDirectory'."
        )
        return
    }

    Write-Host 'Restaging the MXC runtime: the staged files no longer verify.'
}

$cachePath = Join-Path `
    $CacheDirectory `
    "mxc-sdk-$($lock.version).tgz"
$verifiedArchive = Resolve-VerifiedArchive `
    -Lock $lock `
    -SuppliedPath $ArchivePath `
    -CachePath $cachePath

$extractRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "openclaw-mxc-extract-$([guid]::NewGuid().ToString('N'))"
New-Item -Path $extractRoot -ItemType Directory -Force | Out-Null
try {
    # Name every member explicitly so nothing outside the allowlist is written,
    # and so a large archive is not fully expanded.
    $members = @($stagedEntries | ForEach-Object { $_.archivePath })
    & tar -xzf $verifiedArchive -C $extractRoot @members
    if ($LASTEXITCODE -ne 0) {
        throw "Extracting the MXC archive failed with exit code $LASTEXITCODE."
    }

    $stagingDirectory = Join-Path `
        $extractRoot `
        "staged-$([guid]::NewGuid().ToString('N'))"
    New-Item -Path $stagingDirectory -ItemType Directory -Force | Out-Null

    foreach ($entry in $stagedEntries) {
        $extracted = Join-Path $extractRoot ($entry.archivePath -replace '/', '\')
        if (-not (Test-Path -LiteralPath $extracted -PathType Leaf)) {
            throw (
                "The MXC archive does not contain '$($entry.archivePath)'. " +
                'Update mxc-runtime.lock.json before changing the pin.'
            )
        }

        $length = (Get-Item -LiteralPath $extracted).Length
        if ($length -ne $entry.length) {
            throw (
                "'$($entry.archivePath)' is $length bytes; the pin expects " +
                "$($entry.length)."
            )
        }

        $sha256 = Get-Sha256Hex -Path $extracted
        if ($sha256 -ne $entry.sha256) {
            throw (
                "'$($entry.archivePath)' hashes to $sha256; the pin expects " +
                "$($entry.sha256)."
            )
        }

        if ($entry.PSObject.Properties.Name -contains 'peMachine') {
            $machine = Get-PeMachine -Path $extracted
            if ($machine -ne $entry.peMachine) {
                throw (
                    "'$($entry.archivePath)' targets machine $machine; the " +
                    "pin expects $($entry.peMachine) for $Architecture."
                )
            }
        }

        Copy-Item `
            -LiteralPath $extracted `
            -Destination (Join-Path $stagingDirectory $entry.stagedPath) `
            -Force
    }

    [ordered]@{
        package = $lock.package
        version = $lock.version
        architecture = $Architecture
        wireSchemaVersion = $lock.wireSchemaVersion
        minimumWindowsBuild = $lock.minimumWindowsBuild
        tarballIntegrity = $lock.tarballIntegrity
        files = @(
            $stagedEntries |
                ForEach-Object {
                    [ordered]@{
                        path = $_.stagedPath
                        length = $_.length
                        sha256 = $_.sha256
                    }
                } |
                Sort-Object { $_.path }
        )
    } |
        # Keep the nested file records intact.
        ConvertTo-Json -Depth 4 |
        Set-Content `
            -LiteralPath (Join-Path $stagingDirectory 'mxc-runtime.json') `
            -Encoding utf8

    # Publish only after every file verified, so a failed run cannot leave a
    # partially staged runtime behind for the packaging build to pick up.
    if (Test-Path -LiteralPath $OutputDirectory -PathType Container) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }
    New-Item -Path (Split-Path $OutputDirectory -Parent) `
        -ItemType Directory `
        -Force |
        Out-Null
    Move-Item -LiteralPath $stagingDirectory -Destination $OutputDirectory
}
finally {
    if (Test-Path -LiteralPath $extractRoot -PathType Container) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
}

Write-Host (
    "Staged MXC runtime $($lock.package) $($lock.version) ($Architecture) " +
    "in '$OutputDirectory'."
)
