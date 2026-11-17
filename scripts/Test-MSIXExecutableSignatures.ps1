[CmdletBinding(DefaultParameterSetName = 'Publisher')]
param(
    # MSIX packages and/or MSIX bundles. Each package inside a bundle is checked.
    [Parameter(Mandatory)]
    [string[]]$Path,

    [Parameter(Mandatory, ParameterSetName = 'Publisher')]
    [string]$ExpectedPublisher,

    [Parameter(Mandatory, ParameterSetName = 'Thumbprint')]
    [string]$ExpectedThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MSIXExecutableSigning.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Executables that leave the signed package catalog when they run:
# - every executable this repository builds (the session host is staged into
#   the agent's shared workspace) must carry the expected embedded signature;
# - node.exe is extracted from the bundled Node.js archive into the agent
#   profile and must carry a valid signature from its publisher;
# - native artifacts under app\ belong to the upstream payload. Packages under
#   app\node_modules are staged into the agent profile, so an unsigned one is
#   reported for its owner; this repository does not re-sign upstream or
#   third-party code with its own identity.

if (-not $IsWindows) {
    throw 'MSIX executable signature verification requires Windows.'
}

$signatureCheck = if ($PSCmdlet.ParameterSetName -ceq 'Publisher') {
    @{ ExpectedPublisher = $ExpectedPublisher }
}
else {
    @{ ExpectedThumbprint = $ExpectedThumbprint }
}
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw-executable-signatures-$([guid]::NewGuid().ToString('N'))"
)
$failures = [Collections.Generic.List[string]]::new()
$unsignedPayload = [Collections.Generic.List[string]]::new()
$verified = 0

function Save-Entry {
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry,
        [Parameter(Mandatory)][string]$Name
    )

    $target = Join-Path $temporaryRoot ([guid]::NewGuid().ToString('N'))
    New-Item -Path $target -ItemType Directory | Out-Null
    $file = Join-Path $target $Name
    $input = $Entry.Open()
    $outputStream = [IO.File]::Create($file)
    try {
        $input.CopyTo($outputStream)
    }
    finally {
        $outputStream.Dispose()
        $input.Dispose()
    }
    $file
}

function Test-PortableExecutableEntry {
    param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry)

    $stream = $Entry.Open()
    try {
        $header = [byte[]]::new(2)
        $read = $stream.Read($header, 0, 2)
        return $read -eq 2 -and $header[0] -eq 0x4D -and $header[1] -eq 0x5A
    }
    finally {
        $stream.Dispose()
    }
}

function Test-Package {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$Label
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $manifestEntry) {
            $script:failures.Add("$Label has no AppxManifest.xml.")
            return
        }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
        $architecture = [string]$manifest.SelectSingleNode(
            "/*[local-name()='Package']/*[local-name()='Identity']"
        ).ProcessorArchitecture
        if ($architecture -notin @('x64', 'arm64')) {
            $script:failures.Add("$Label has unsupported architecture '$architecture'.")
            return
        }

        $owned = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $nodeArchives = 0
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }
            $entryPath = [Uri]::UnescapeDataString($entry.FullName)
            $name = "$Label $entryPath"

            if (Test-MSIXOwnedExecutable -PackagePath $entryPath -Architecture $architecture) {
                [void]$owned.Add($entryPath)
                $file = Save-Entry -Entry $entry -Name ([IO.Path]::GetFileName($entryPath))
                try {
                    $null = Assert-ExecutableSignature -Path $file -Name $name @script:signatureCheck
                    $script:verified++
                }
                catch {
                    $script:failures.Add($_.Exception.Message)
                }
                continue
            }

            if ($entryPath -match '^runtime/node-v[^/]+\.zip$') {
                $nodeArchives++
                $archiveFile = Save-Entry -Entry $entry -Name ([IO.Path]::GetFileName($entryPath))
                $nodeArchive = [IO.Compression.ZipFile]::OpenRead($archiveFile)
                try {
                    $nodeEntries = @(
                        $nodeArchive.Entries |
                            Where-Object { $_.Name -ceq 'node.exe' }
                    )
                    if ($nodeEntries.Count -ne 1) {
                        $script:failures.Add("$name must contain exactly one node.exe.")
                        continue
                    }
                    $nodeFile = Save-Entry -Entry $nodeEntries[0] -Name 'node.exe'
                    $signature = Get-AuthenticodeSignature -LiteralPath $nodeFile
                    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') {
                        $script:failures.Add(
                            "$name node.exe has no valid embedded signature ($($signature.Status))."
                        )
                    }
                    else {
                        $script:verified++
                    }
                }
                finally {
                    $nodeArchive.Dispose()
                }
                continue
            }

            if (
                $entryPath.StartsWith('app/', [StringComparison]::Ordinal) -and
                [IO.Path]::GetExtension($entryPath) -in @('.node', '.dll', '.exe') -and
                (Test-PortableExecutableEntry -Entry $entry)
            ) {
                $file = Save-Entry -Entry $entry -Name ([IO.Path]::GetFileName($entryPath))
                $signature = Get-AuthenticodeSignature -LiteralPath $file
                if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') {
                    $script:unsignedPayload.Add("$name ($($signature.Status))")
                }
            }
        }

        foreach ($required in @(
                'openclaw.exe',
                'openclaw-gateway-recovery.exe',
                "session-host/$architecture/openclaw-session-host.exe"
            )) {
            if (-not $owned.Contains($required)) {
                $script:failures.Add("$Label has no $required.")
            }
        }
        if ($nodeArchives -ne 1) {
            $script:failures.Add("$Label must contain exactly one Node.js runtime archive.")
        }
    }
    finally {
        $archive.Dispose()
    }
}

try {
    New-Item -Path $temporaryRoot -ItemType Directory | Out-Null
    foreach ($candidate in $Path) {
        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        switch ([IO.Path]::GetExtension($resolved).ToLowerInvariant()) {
            '.msix' {
                Test-Package -PackagePath $resolved -Label ([IO.Path]::GetFileName($resolved))
            }
            '.msixbundle' {
                $bundle = [IO.Compression.ZipFile]::OpenRead($resolved)
                try {
                    $packages = @($bundle.Entries | Where-Object { $_.FullName -like '*.msix' })
                    if ($packages.Count -eq 0) {
                        $failures.Add("$resolved contains no MSIX packages.")
                    }
                    foreach ($package in $packages) {
                        $packageFile = Save-Entry -Entry $package -Name $package.Name
                        Test-Package `
                            -PackagePath $packageFile `
                            -Label "$([IO.Path]::GetFileName($resolved))/$($package.Name)"
                    }
                }
                finally {
                    $bundle.Dispose()
                }
            }
            default {
                throw "Expected an .msix or .msixbundle path: $resolved"
            }
        }
    }
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

foreach ($unsigned in $unsignedPayload) {
    Write-Warning "Unsigned upstream payload native artifact: $unsigned"
}

if ($failures.Count -ne 0) {
    throw (
        "MSIX executable signature verification failed:`n  " +
        ($failures -join "`n  ")
    )
}

Write-Host "Verified $verified executable signatures across $($Path.Count) MSIX artifact(s)."
