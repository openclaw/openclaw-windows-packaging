# Replaces Destination with a copy of an expanded payload tree.
# Copy-Item takes minutes for the ~38,000-file OpenClaw tree; robocopy's
# multithreaded copy takes seconds. robocopy /XJ silently skips links rather
# than failing, so a link below the source is rejected up front. An existing
# destination is removed first so robocopy never merges into or skips
# "unchanged" stale content, and paths that overlap after resolving links are
# refused before anything is removed, so the source is never modified.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Source,

    [Parameter(Mandatory)]
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# robocopy reports a successful copy with exit code 1.
$PSNativeCommandUseErrorActionPreference = $false

function Resolve-CopyPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::TrimEndingDirectorySeparator(
        [IO.Path]::GetFullPath(
            $Path,
            (Get-Location -PSProvider FileSystem).ProviderPath))
    # Never copy a whole drive or remove a drive root as a destination.
    if ([string]::Equals(
            $fullPath,
            [IO.Path]::TrimEndingDirectorySeparator(
                [IO.Path]::GetPathRoot($fullPath)),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "A payload copy path must not be a filesystem root: $fullPath"
    }
    $fullPath
}

function Resolve-PhysicalPath {
    # Follows every link along the path, so a junctioned parent or a linked
    # source root cannot disguise overlapping directories.
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolved = [IO.Path]::GetPathRoot($Path)
    $segments = $Path.Substring($resolved.Length).Split(
        [IO.Path]::DirectorySeparatorChar,
        [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($segment in $segments) {
        $resolved = [IO.Path]::Combine($resolved, $segment)
        $linkTarget = [IO.DirectoryInfo]::new($resolved).LinkTarget
        if ($null -ne $linkTarget) {
            $resolved = Resolve-PhysicalPath -Path (
                [IO.Path]::GetFullPath(
                    $linkTarget,
                    [IO.Path]::GetDirectoryName($resolved)))
        }
    }
    [IO.Path]::TrimEndingDirectorySeparator($resolved)
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Parent
    )

    $relativePath = [IO.Path]::GetRelativePath($Parent, $Path)
    -not [IO.Path]::IsPathRooted($relativePath) -and
        $relativePath -ne '..' -and
        -not $relativePath.StartsWith(
            '..' + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::Ordinal)
}

$sourcePath = Resolve-CopyPath -Path $Source
$destinationPath = Resolve-CopyPath -Path $Destination

if (-not [IO.Directory]::Exists($sourcePath)) {
    throw "The payload copy source is not a directory: $sourcePath"
}
$physicalSource = Resolve-PhysicalPath -Path $sourcePath
$physicalDestination = Resolve-PhysicalPath -Path $destinationPath
if ((Test-PathWithin -Path $physicalDestination -Parent $physicalSource) -or
    (Test-PathWithin -Path $physicalSource -Parent $physicalDestination)) {
    throw (
        "The payload copy source '$sourcePath' and destination " +
        "'$destinationPath' must not overlap."
    )
}

$enumerationOptions = [IO.EnumerationOptions]::new()
$enumerationOptions.RecurseSubdirectories = $true
$enumerationOptions.AttributesToSkip = [IO.FileAttributes]::None
$enumerationOptions.IgnoreInaccessible = $false
foreach ($entry in [IO.DirectoryInfo]::new($sourcePath).EnumerateFileSystemInfos(
        '*',
        $enumerationOptions)) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw (
            'The payload copy source must not contain links or reparse points: ' +
            [IO.Path]::GetRelativePath($sourcePath, $entry.FullName)
        )
    }
}

$existingDestination = Get-Item -LiteralPath $destinationPath -Force -ErrorAction Ignore
if ($null -ne $existingDestination) {
    if ($existingDestination -isnot [IO.DirectoryInfo]) {
        throw "The payload copy destination is not a directory: $destinationPath"
    }
    # Neither removal follows links inside the destination; a linked
    # destination root is removed as a link.
    try {
        [IO.Directory]::Delete($destinationPath, $true)
    }
    catch [UnauthorizedAccessException] {
        # Directory.Delete refuses read-only files and reports a nested
        # junction as denied after unlinking it; Remove-Item -Force finishes.
        Remove-Item -LiteralPath $destinationPath -Recurse -Force
    }
}

$robocopy = Join-Path ([Environment]::SystemDirectory) 'robocopy.exe'
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$robocopyOutput = @(
    & $robocopy $sourcePath $destinationPath `
        /E /MT:16 /XJ /R:0 /W:0 /NP /NFL /NDL /NJH /NJS
)
$exitCode = $LASTEXITCODE
$stopwatch.Stop()

# robocopy exit codes are flags: 1 copied, 2 extra, 4 mismatched, 8 failed,
# 16 fatal. The destination is always new, so only 0 (empty source) and 1 are
# a complete copy; extra or mismatched entries mean it changed during the copy.
if ($exitCode -notin 0, 1) {
    $details = @(
        $robocopyOutput |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { $_ }
    )
    throw (
        "robocopy could not copy '$sourcePath' to '$destinationPath' " +
        "(exit code $exitCode)." +
        $(if ($details.Count -ne 0) {
            [Environment]::NewLine + ($details -join [Environment]::NewLine)
        })
    )
}

Write-Host (
    "Copied '$sourcePath' to '$destinationPath' in " +
    $stopwatch.Elapsed.TotalSeconds.ToString(
        '0.0',
        [Globalization.CultureInfo]::InvariantCulture) +
    ' s.'
)
