[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Copy-PayloadTree.ps1'
# The space in the fixture root exercises robocopy argument quoting.
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw payload-copy-$([guid]::NewGuid().ToString('N'))"
)

function Assert-Fails {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw
        }
        return
    }
    throw "Expected failure matching '$MessagePattern'."
}

function Get-TreeInventory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $options = [IO.EnumerationOptions]::new()
    $options.RecurseSubdirectories = $true
    $options.AttributesToSkip = [IO.FileAttributes]::None
    $preservedAttributes = [IO.FileAttributes]::Hidden -bor
        [IO.FileAttributes]::ReadOnly
    @(
        foreach ($entry in [IO.DirectoryInfo]::new($Path).EnumerateFileSystemInfos(
                '*',
                $options)) {
            $relativePath = [IO.Path]::GetRelativePath($Path, $entry.FullName)
            if ($entry -is [IO.DirectoryInfo]) {
                "directory|$relativePath"
                continue
            }
            @(
                'file'
                $relativePath
                $entry.Length
                (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash
                [int]($entry.Attributes -band $preservedAttributes)
                $entry.LastWriteTimeUtc.Ticks
            ) -join '|'
        }
    ) | Sort-Object
}

function Assert-TreeEquals {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Reason
    )

    $actual = @(Get-TreeInventory -Path $Path)
    $difference = @(Compare-Object -ReferenceObject $Expected -DifferenceObject $actual)
    if ($actual.Count -ne $Expected.Count -or $difference.Count -ne 0) {
        throw (
            "$Reason. Differences: " +
            (($difference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join '; ')
        )
    }
}

function New-SourceTree {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $longDirectory = $Path
    foreach ($index in 1..6) {
        $longDirectory = Join-Path `
            $longDirectory `
            "node_modules\dependency-with-a-long-package-name-$index"
    }
    foreach ($directory in @(
        (Join-Path $Path 'dist\extensions\fixture')
        (Join-Path $Path 'empty directory')
        $longDirectory
    )) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    [IO.File]::WriteAllText((Join-Path $Path 'openclaw.mjs'), 'export {};')
    [IO.File]::WriteAllText((Join-Path $Path 'package.json'), 'AAAA')
    [IO.File]::WriteAllBytes(
        (Join-Path $Path 'dist\extensions\fixture\native addon.node'),
        [byte[]](0..255))
    $unicodeName = "m$([char]0x00FC)nchen-$([char]0x6F22).js"
    [IO.File]::WriteAllText((Join-Path $Path "dist\$unicodeName"), 'unicode')
    $longFile = Join-Path $longDirectory 'index.js'
    [IO.File]::WriteAllText($longFile, 'long path')
    if ($longFile.Length -le 260) {
        throw "The long-path fixture is only $($longFile.Length) characters."
    }

    $hiddenFile = Join-Path $Path 'dist\.hidden-marker'
    [IO.File]::WriteAllText($hiddenFile, 'hidden')
    [IO.File]::SetAttributes($hiddenFile, [IO.FileAttributes]::Hidden)
    $readOnlyFile = Join-Path $Path 'dist\read-only.js'
    [IO.File]::WriteAllText($readOnlyFile, 'read only')
    [IO.File]::SetAttributes($readOnlyFile, [IO.FileAttributes]::ReadOnly)
}

function New-Junction {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Target
    )

    New-Item -ItemType Junction -Path $Path -Target $Target | Out-Null
}

function New-OutsideDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    [IO.Directory]::CreateDirectory($Path) | Out-Null
    [IO.File]::WriteAllText((Join-Path $Path 'sentinel.txt'), 'outside')
    @(Get-TreeInventory -Path $Path)
}

New-Item -Path $testRoot -ItemType Directory | Out-Null
try {
    $source = Join-Path $testRoot 'source\app'
    New-SourceTree -Path $source
    $sourceInventory = @(Get-TreeInventory -Path $source)

    # A faithful copy keeps every file, including hidden, read-only, Unicode,
    # and long-path entries, with its bytes, attributes, and timestamps.
    $copy = Join-Path $testRoot 'content\openclaw\app'
    $copyOutput = @(& $scriptPath -Source $source -Destination $copy 6>&1)
    Assert-TreeEquals `
        -Expected $sourceInventory `
        -Path $copy `
        -Reason 'The copy does not match the source'
    if (-not ($copyOutput | Where-Object {
            "$_" -match "^Copied '.+' to '.+' in \d+\.\d s\.$"
        })) {
        throw "The copy did not report its duration: $($copyOutput -join '; ')"
    }
    Assert-TreeEquals `
        -Expected $sourceInventory `
        -Path $source `
        -Reason 'The copy changed its source'

    $emptySource = Join-Path $testRoot 'empty-source'
    [IO.Directory]::CreateDirectory($emptySource) | Out-Null
    $emptyCopy = Join-Path $testRoot 'empty-copy'
    & $scriptPath -Source $emptySource -Destination $emptyCopy | Out-Null
    if (-not [IO.Directory]::Exists($emptyCopy) -or
        @([IO.Directory]::EnumerateFileSystemEntries($emptyCopy)).Count -ne 0) {
        throw 'An empty source did not produce an empty destination.'
    }

    # An existing destination is replaced, not merged: stale files disappear,
    # and a same-size, same-timestamp file with different content is rewritten.
    # Removing it must not follow a junction out of the destination.
    $staleDestination = Join-Path $testRoot 'stale-destination'
    $staleOutside = Join-Path $testRoot 'stale-outside'
    $staleOutsideInventory = New-OutsideDirectory -Path $staleOutside
    [IO.Directory]::CreateDirectory($staleDestination) | Out-Null
    [IO.File]::WriteAllText((Join-Path $staleDestination 'stale.js'), 'stale')
    $stalePackage = Join-Path $staleDestination 'package.json'
    [IO.File]::WriteAllText($stalePackage, 'BBBB')
    [IO.File]::SetLastWriteTimeUtc(
        $stalePackage,
        [IO.File]::GetLastWriteTimeUtc((Join-Path $source 'package.json')))
    New-Junction -Path (Join-Path $staleDestination 'linked') -Target $staleOutside
    & $scriptPath -Source $source -Destination $staleDestination | Out-Null
    Assert-TreeEquals `
        -Expected $sourceInventory `
        -Path $staleDestination `
        -Reason 'Replacing an existing destination left stale content'
    Assert-TreeEquals `
        -Expected $staleOutsideInventory `
        -Path $staleOutside `
        -Reason 'Replacing a destination changed a junction target'

    # Read-only files in an existing destination do not block replacement.
    $readOnlyDestination = Join-Path $testRoot 'read-only-destination'
    $readOnlyOutside = Join-Path $testRoot 'read-only-outside'
    $readOnlyOutsideInventory = New-OutsideDirectory -Path $readOnlyOutside
    & $scriptPath -Source $source -Destination $readOnlyDestination | Out-Null
    New-Junction -Path (Join-Path $readOnlyDestination 'linked') -Target $readOnlyOutside
    & $scriptPath -Source $source -Destination $readOnlyDestination | Out-Null
    Assert-TreeEquals `
        -Expected $sourceInventory `
        -Path $readOnlyDestination `
        -Reason 'Replacing a destination with read-only files failed'
    Assert-TreeEquals `
        -Expected $readOnlyOutsideInventory `
        -Path $readOnlyOutside `
        -Reason 'Replacing a read-only destination changed a junction target'

    # A destination that is itself a junction is replaced by a real directory;
    # the junction target keeps its content.
    $linkedDestination = Join-Path $testRoot 'linked-destination'
    $linkedOutside = Join-Path $testRoot 'linked-outside'
    $linkedOutsideInventory = New-OutsideDirectory -Path $linkedOutside
    New-Junction -Path $linkedDestination -Target $linkedOutside
    & $scriptPath -Source $source -Destination $linkedDestination | Out-Null
    if ((Get-Item -LiteralPath $linkedDestination -Force).LinkType) {
        throw 'A linked destination was not replaced by a real directory.'
    }
    Assert-TreeEquals `
        -Expected $sourceInventory `
        -Path $linkedDestination `
        -Reason 'Replacing a linked destination did not copy the source'
    Assert-TreeEquals `
        -Expected $linkedOutsideInventory `
        -Path $linkedOutside `
        -Reason 'Replacing a linked destination changed its target'

    # robocopy /XJ would silently drop a link, so a link below the source
    # fails before the existing destination is touched.
    $linkedSource = Join-Path $testRoot 'linked-source'
    New-SourceTree -Path $linkedSource
    $linkedSourceOutside = Join-Path $testRoot 'linked-source-outside'
    New-OutsideDirectory -Path $linkedSourceOutside | Out-Null
    New-Junction `
        -Path (Join-Path $linkedSource 'dist\linked') `
        -Target $linkedSourceOutside
    $untouchedDestination = Join-Path $testRoot 'untouched-destination'
    $untouchedInventory = New-OutsideDirectory -Path $untouchedDestination
    Assert-Fails -MessagePattern 'must not contain links or reparse points: dist\\linked' -Action {
        & $scriptPath -Source $linkedSource -Destination $untouchedDestination
    }
    Assert-TreeEquals `
        -Expected $untouchedInventory `
        -Path $untouchedDestination `
        -Reason 'A rejected source changed the existing destination'

    # Overlapping paths are refused before anything is removed, including
    # overlap that only appears after resolving a junctioned parent or a
    # linked source root.
    $aliasRoot = Join-Path $testRoot 'alias'
    New-Junction -Path $aliasRoot -Target (Join-Path $testRoot 'source')
    $linkedApplication = Join-Path $testRoot 'linked-application'
    New-Junction -Path $linkedApplication -Target $copy
    $copyInventory = @(Get-TreeInventory -Path $copy)
    foreach ($overlap in @(
        @{ Source = $source; Destination = $source }
        @{ Source = $source; Destination = (Join-Path $source 'dist\copy') }
        @{ Source = (Join-Path $source 'dist'); Destination = $source }
        @{ Source = $source; Destination = (Join-Path $aliasRoot 'app') }
        @{ Source = $linkedApplication; Destination = $copy }
    )) {
        Assert-Fails -MessagePattern 'must not overlap' -Action {
            & $scriptPath @overlap
        }
    }
    Assert-TreeEquals `
        -Expected $sourceInventory `
        -Path $source `
        -Reason 'A refused overlapping copy changed the source'
    Assert-TreeEquals `
        -Expected $copyInventory `
        -Path $copy `
        -Reason 'A refused copy through a linked source root changed its target'

    # robocopy failures are reported with its exit code and error text.
    $lockedFile = [IO.File]::Open(
        (Join-Path $source 'package.json'),
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    try {
        Assert-Fails -MessagePattern '(?s)exit code (8|9|1\d).*0x00000020' -Action {
            & $scriptPath -Source $source -Destination (Join-Path $testRoot 'locked-copy')
        }
    }
    finally {
        $lockedFile.Dispose()
    }

    Assert-Fails -MessagePattern 'source is not a directory' -Action {
        & $scriptPath `
            -Source (Join-Path $testRoot 'missing') `
            -Destination (Join-Path $testRoot 'missing-copy')
    }

    $fileDestination = Join-Path $testRoot 'file-destination'
    [IO.File]::WriteAllText($fileDestination, 'keep')
    Assert-Fails -MessagePattern 'destination is not a directory' -Action {
        & $scriptPath -Source $source -Destination $fileDestination
    }
    if ([IO.File]::ReadAllText($fileDestination) -cne 'keep') {
        throw 'A refused file destination was changed.'
    }

    # An unused drive letter keeps a regression from reaching a real drive.
    $usedDriveLetters = @([IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name[0] })
    $unusedDriveLetter = @(
        [char[]]'QRSTUVWXYZJKLMNOP' |
            Where-Object { $_ -notin $usedDriveLetters }
    )[0]
    $unusedDriveRoot = "$($unusedDriveLetter):\"
    Assert-Fails -MessagePattern 'must not be a filesystem root' -Action {
        & $scriptPath -Source $source -Destination $unusedDriveRoot
    }
    Assert-Fails -MessagePattern 'must not be a filesystem root' -Action {
        & $scriptPath -Source $unusedDriveRoot -Destination (Join-Path $testRoot 'root-copy')
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Payload tree copy tests passed.'
