#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies the official MXC SDK package and returns its native inventory.
.DESCRIPTION
    NuGet restore owns acquisition. This operation checks the resolved package
    against mxc-runtime.lock.json, including archive integrity, the managed
    assembly, and the complete selected Windows native unit. It never downloads
    or executes a runtime. Optional published output is checked against the same
    pin. OutputDirectory receives only verified license/provenance content.
.PARAMETER PackageDirectory
    Resolved Microsoft.Mxc.Sdk package directory. When omitted, MSBuild's
    generated package-path property is read from the restored launcher.
.PARAMETER RuntimeDirectory
    Optional publish/layout root whose native files must match the package.
.PARAMETER OutputDirectory
    Optional directory for mxc-runtime.json and mxc-LICENSE.md.
.PARAMETER IncludePackagingContent
    Read the package property from the isolated packaging restore.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$PackageDirectory,
    [string]$RuntimeDirectory,
    [string]$OutputDirectory,
    [switch]$IncludePackagingContent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$lock = Get-Content -LiteralPath (Join-Path $repositoryRoot 'mxc-runtime.lock.json') -Raw |
    ConvertFrom-Json
[xml]$central = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Packages.props') -Raw
$versions = @($central.SelectNodes('//PackageVersion[@Include="Microsoft.Mxc.Sdk"]'))
if (
    $versions.Count -ne 1 -or
    [string]$versions[0].Version -cne [string]$lock.version -or
    $lock.package -cne 'Microsoft.Mxc.Sdk'
) {
    throw 'The MXC integrity manifest does not match the central SDK package version.'
}

function Assert-SafePath {
    param([string]$Path, [switch]$Flat)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'The MXC inventory contains an empty path.' }
    $segments = @($Path.Split('/'))
    if (
        [string]::IsNullOrWhiteSpace($Path) -or
        [IO.Path]::IsPathRooted($Path) -or $Path.Contains('\') -or $Path.Contains(':') -or
        $segments -contains '' -or $segments -contains '.' -or $segments -contains '..' -or
        ($Flat -and $segments.Count -ne 1)
    ) {
        throw "Unsafe MXC inventory path: '$Path'."
    }
}

function Assert-PinnedFile {
    param([string]$Path, $Pin)

    if (
        -not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        $Pin.length -isnot [int64] -or $Pin.length -lt 0 -or
        $Pin.sha256 -notmatch '^[0-9a-f]{64}$' -or
        (Get-Item -LiteralPath $Path).Length -ne $Pin.length -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Pin.sha256
    ) {
        throw "The MXC file does not match its integrity pin: '$Path'."
    }
    if ($Pin.PSObject.Properties.Name -contains 'peMachine') {
        $stream = [IO.File]::OpenRead($Path)
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5a4d) { throw "'$Path' is not a PE image." }
            $stream.Position = 0x3c
            $offset = $reader.ReadUInt32()
            $stream.Position = $offset
            if ($reader.ReadUInt32() -ne 0x00004550) { throw "'$Path' has no PE signature." }
            $machine = '0x{0:x4}' -f $reader.ReadUInt16()
            if ($machine -cne $Pin.peMachine) {
                throw "The MXC file '$Path' has PE machine $machine, not $($Pin.peMachine)."
            }
        }
        finally { $reader.Dispose(); $stream.Dispose() }
    }
}

if (-not $PackageDirectory) {
    $project = Join-Path $repositoryRoot 'src\OpenClaw.Launcher\OpenClaw.Launcher.csproj'
    $packaging = if ($IncludePackagingContent) { 'true' } else { 'false' }
    $lines = @(& dotnet msbuild $project -nologo -getProperty:PkgMicrosoft_Mxc_Sdk `
        "-p:IncludePackagingContent=$packaging" "-p:RuntimeIdentifier=win-$Architecture" `
        "-p:Platform=$Architecture")
    if ($LASTEXITCODE -ne 0) { throw "Resolving the MXC NuGet package failed (exit $LASTEXITCODE)." }
    $PackageDirectory = [string]::Join("`n", [string[]]$lines).Trim()
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    throw 'The MXC SDK has not been restored. Restore the launcher for the selected build lane.'
}
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$archivePath = Join-Path $PackageDirectory "microsoft.mxc.sdk.$($lock.version).nupkg"
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash
if ([Convert]::ToBase64String([Convert]::FromHexString($archiveHash)) -cne $lock.archiveSha512) {
    throw 'The restored MXC NuGet archive does not match its integrity pin.'
}

$pins = @($lock.architectures.$Architecture.files)
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($pin in $pins) {
    Assert-SafePath -Path $pin.archivePath
    Assert-SafePath -Path $pin.stagedPath -Flat
    if (
        $pin.archivePath -cne "runtimes/win-$Architecture/native/$($pin.stagedPath)" -or
        -not $names.Add([string]$pin.stagedPath)
    ) {
        throw 'The MXC native inventory contains duplicate or incorrectly routed paths.'
    }
}
$required = @('mxc_ffi.dll', 'plm.exe', 'wslcsdk.dll', 'wxc-wslc-daemon.exe')
if ($names.Count -ne $required.Count -or @($required | Where-Object { -not $names.Contains($_) }).Count) {
    throw 'The MXC integrity manifest must name the complete Windows SDK native unit.'
}
$expectedMachine = if ($Architecture -eq 'x64') { '0x8664' } else { '0xaa64' }
if (@($pins | Where-Object { $_.peMachine -cne $expectedMachine }).Count) {
    throw 'The MXC native integrity pins do not match the selected architecture.'
}
$allPins = @($lock.managedAssembly) + $pins + @($lock.licenseFiles)
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        if (-not $entry.Name) { continue }
        Assert-SafePath -Path $entry.FullName
        if (-not $entries.TryAdd($entry.FullName, $entry)) {
            throw "The MXC NuGet archive contains a duplicate path: $($entry.FullName)."
        }
    }
    $nativePrefix = "runtimes/win-$Architecture/native/"
    $nativeEntries = @($entries.Keys | Where-Object { $_.StartsWith($nativePrefix, [StringComparison]::Ordinal) })
    if (
        $nativeEntries.Count -ne $pins.Count -or
        @($nativeEntries | Where-Object { $_ -cnotin $pins.archivePath }).Count
    ) {
        throw 'The MXC NuGet archive contains an unexpected native file set.'
    }
    foreach ($pin in $allPins) {
        Assert-SafePath -Path $pin.archivePath
        if (-not $entries.ContainsKey([string]$pin.archivePath)) {
            throw "The MXC NuGet archive lacks '$($pin.archivePath)'."
        }
        $entry = $entries[[string]$pin.archivePath]
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($entry.Length -ne $pin.length -or $hash -cne $pin.sha256) {
            throw "The archived MXC file does not match its integrity pin: '$($pin.archivePath)'."
        }
        Assert-PinnedFile -Path (Join-Path $PackageDirectory $pin.archivePath.Replace('/', '\')) -Pin $pin
    }
}
finally { $archive.Dispose() }

[xml]$nuspec = Get-Content -LiteralPath (Join-Path $PackageDirectory 'microsoft.mxc.sdk.nuspec') -Raw
$metadata = $nuspec.SelectSingleNode('//*[local-name()="metadata"]')
if (
    $metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $lock.package -or
    $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $lock.version
) {
    throw 'The restored MXC package metadata has a different identity.'
}

$files = @(
    foreach ($pin in $pins) {
        $source = Join-Path $PackageDirectory $pin.archivePath.Replace('/', '\')
        if ($RuntimeDirectory) {
            Assert-PinnedFile -Path (Join-Path $RuntimeDirectory $pin.stagedPath) -Pin $pin
            $source = Join-Path $RuntimeDirectory $pin.stagedPath
        }
        [pscustomobject]@{
            path = [string]$pin.stagedPath
            length = [long]$pin.length
            sha256 = [string]$pin.sha256
            sourcePath = $source
        }
    }
)
if ($RuntimeDirectory) {
    $allowed = @($required) + @('mxc-runtime.json', 'mxc-LICENSE.md')
    $unexpected = @(Get-ChildItem -LiteralPath $RuntimeDirectory -File | Where-Object {
        $_.Name -match '^(?:mxc[_-]|plm[.]|wslcsdk[.]|wxc-)' -and $_.Name -notin $allowed
    })
    if ($unexpected.Count -or (Test-Path -LiteralPath (Join-Path $RuntimeDirectory 'mxc'))) {
        throw 'The published MXC unit contains unexpected or retired runtime content.'
    }
}

$provenance = [ordered]@{
    package = $lock.package
    version = $lock.version
    architecture = $Architecture
    archiveSha512 = $lock.archiveSha512
    files = @($files | Select-Object path, length, sha256)
}
if ($OutputDirectory) {
    New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null
    foreach ($pin in @($lock.licenseFiles)) {
        Assert-SafePath -Path $pin.stagedPath -Flat
        $destination = Join-Path $OutputDirectory $pin.stagedPath
        Copy-Item -LiteralPath (Join-Path $PackageDirectory $pin.archivePath.Replace('/', '\')) -Destination $destination -Force
        Assert-PinnedFile -Path $destination -Pin $pin
        $files += [pscustomobject]@{
            path = [string]$pin.stagedPath
            length = [long]$pin.length
            sha256 = [string]$pin.sha256
            sourcePath = $destination
        }
    }
    $provenancePath = Join-Path $OutputDirectory 'mxc-runtime.json'
    $json = ($provenance | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
    [IO.File]::WriteAllText($provenancePath, $json, [Text.UTF8Encoding]::new($false))
    $files += [pscustomobject]@{
        path = 'mxc-runtime.json'
        length = (Get-Item -LiteralPath $provenancePath).Length
        sha256 = (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash.ToLowerInvariant()
        sourcePath = $provenancePath
    }
}

[pscustomobject]@{
    PackageDirectory = $PackageDirectory
    Provenance = [pscustomobject]$provenance
    Files = @($files | Sort-Object path)
}
