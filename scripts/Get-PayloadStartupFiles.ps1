[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ApplicationDirectory,

    [ValidateRange(1, 100000)]
    [int]$MaximumFiles = 8000
)

# Approximates the payload files the Gateway reaches at startup, so that
# `clawctl setup` can read them once in the background and let on-access
# antivirus scan them before the first Gateway start. The list is a best-effort
# prewarm hint, not an inventory: it follows static `import`/`export ... from`,
# literal `import()`, and literal `require()` specifiers from openclaw.mjs, then
# from each bundled extension's index file. Computed specifiers, conditional
# runtime paths, and filesystem plugin discovery beyond those index files are
# not followed, and literal imports that are deferred at runtime are included.
# Output order is breadth-first, so truncation at -MaximumFiles keeps the files
# closest to the entry point.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$applicationRoot = [IO.Path]::GetFullPath($ApplicationDirectory).TrimEnd('\', '/')
$entryPoint = Join-Path $applicationRoot 'openclaw.mjs'
if (-not (Test-Path -LiteralPath $entryPoint -PathType Leaf)) {
    throw "The application directory does not contain openclaw.mjs: $applicationRoot"
}

$parsedExtensions = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($extension in '.js', '.mjs', '.cjs') {
    [void]$parsedExtensions.Add($extension)
}
$specifierPattern = [regex]::new(
    '(?:\bimport\s*(?:[\w$*{}\s,]*?\bfrom\s*)?|' +
    '\bexport\s*(?:\*\s*(?:as\s+[\w$]+\s*)?|\{[^}]*\}\s*)from\s*|' +
    '\bimport\s*\(\s*|\brequire\s*\(\s*)' +
    '(["''])([^"''\r\n]+)\1',
    [Text.RegularExpressions.RegexOptions]::Compiled
)
$conditions = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal
)
foreach ($condition in 'node', 'import', 'require', 'module-sync', 'default') {
    [void]$conditions.Add($condition)
}
$packageCache = @{}

function Test-UnderApplication {
    param([Parameter(Mandatory)][string]$Path)

    $relative = [IO.Path]::GetRelativePath($applicationRoot, $Path)
    return -not (
        $relative -eq '..' -or
        $relative.StartsWith('..\') -or
        $relative.StartsWith('../') -or
        [IO.Path]::IsPathRooted($relative)
    )
}

function Read-PackageManifest {
    param([Parameter(Mandatory)][string]$Directory)

    if ($packageCache.ContainsKey($Directory)) {
        return $packageCache[$Directory]
    }
    $manifest = $null
    $manifestPath = Join-Path $Directory 'package.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw |
                ConvertFrom-Json
        }
        catch {
            $manifest = $null
        }
    }
    $packageCache[$Directory] = $manifest
    return $manifest
}

function Get-JsonProperty {
    param($Object, [Parameter(Mandatory)][string]$Name)

    if ($Object -isnot [pscustomobject]) {
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Resolve-FileCandidate {
    param([Parameter(Mandatory)][string]$Path)

    $Path = [IO.Path]::GetFullPath($Path)
    if (-not (Test-UnderApplication -Path $Path)) {
        return $null
    }
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return $Path
    }
    foreach ($extension in '.js', '.mjs', '.cjs', '.json') {
        if (Test-Path -LiteralPath "$Path$extension" -PathType Leaf) {
            return "$Path$extension"
        }
    }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        $main = Get-JsonProperty -Object (Read-PackageManifest -Directory $Path) -Name 'main'
        if ($main -is [string] -and -not [string]::IsNullOrWhiteSpace($main)) {
            $resolvedMain = Resolve-FileCandidate -Path (Join-Path $Path $main)
            if ($null -ne $resolvedMain) {
                return $resolvedMain
            }
        }
        foreach ($index in 'index.js', 'index.mjs', 'index.cjs', 'index.json') {
            $candidate = Join-Path $Path $index
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }
    return $null
}

function Resolve-ExportTarget {
    param($Target, [string]$Star)

    if ($null -eq $Target) {
        return $null
    }
    if ($Target -is [string]) {
        if ($null -ne $Star) {
            return $Target.Replace('*', $Star)
        }
        return $Target
    }
    if ($Target -is [array]) {
        foreach ($item in $Target) {
            $resolved = Resolve-ExportTarget -Target $item -Star $Star
            if ($null -ne $resolved) {
                return $resolved
            }
        }
        return $null
    }
    if ($Target -is [pscustomobject]) {
        foreach ($property in $Target.PSObject.Properties) {
            if ($conditions.Contains($property.Name)) {
                $resolved = Resolve-ExportTarget -Target $property.Value -Star $Star
                if ($null -ne $resolved) {
                    return $resolved
                }
            }
        }
    }
    return $null
}

function Resolve-PackageExports {
    param($Exports, [Parameter(Mandatory)][string]$Key)

    $isSubpathMap = $Exports -is [pscustomobject] -and
        @($Exports.PSObject.Properties).Count -gt 0 -and
        @($Exports.PSObject.Properties | Where-Object { -not $_.Name.StartsWith('.') }).Count -eq 0
    if (-not $isSubpathMap) {
        if ($Key -ne '.') {
            return $null
        }
        return Resolve-ExportTarget -Target $Exports -Star $null
    }

    $exact = $Exports.PSObject.Properties[$Key]
    if ($null -ne $exact) {
        return Resolve-ExportTarget -Target $exact.Value -Star $null
    }
    $bestPrefix = $null
    $bestValue = $null
    $bestStar = $null
    foreach ($property in $Exports.PSObject.Properties) {
        $starIndex = $property.Name.IndexOf('*')
        if ($starIndex -lt 0) {
            continue
        }
        $prefix = $property.Name.Substring(0, $starIndex)
        $suffix = $property.Name.Substring($starIndex + 1)
        if (
            $Key.Length -ge $prefix.Length + $suffix.Length -and
            $Key.StartsWith($prefix, [StringComparison]::Ordinal) -and
            $Key.EndsWith($suffix, [StringComparison]::Ordinal) -and
            ($null -eq $bestPrefix -or $prefix.Length -gt $bestPrefix.Length)
        ) {
            $bestPrefix = $prefix
            $bestValue = $property.Value
            $bestStar = $Key.Substring(
                $prefix.Length,
                $Key.Length - $prefix.Length - $suffix.Length
            )
        }
    }
    if ($null -eq $bestPrefix) {
        return $null
    }
    return Resolve-ExportTarget -Target $bestValue -Star $bestStar
}

function Resolve-Specifier {
    param(
        [Parameter(Mandatory)][string]$Importer,
        [Parameter(Mandatory)][string]$Specifier
    )

    $Specifier = $Specifier.Split('?')[0].Split('#')[0]
    if (
        [string]::IsNullOrWhiteSpace($Specifier) -or
        $Specifier -match '^[a-zA-Z][a-zA-Z0-9+.-]*:' -or
        $Specifier.StartsWith('/') -or
        $Specifier.StartsWith('\')
    ) {
        return @()
    }

    $importerDirectory = [IO.Path]::GetDirectoryName($Importer)
    if ($Specifier -match '^\.\.?(?:/|$)') {
        $resolved = Resolve-FileCandidate -Path (Join-Path $importerDirectory $Specifier)
        if ($null -eq $resolved) {
            return @()
        }
        return @($resolved)
    }

    $segments = $Specifier.Split('/')
    $nameLength = if ($Specifier.StartsWith('@')) { 2 } else { 1 }
    if ($segments.Count -lt $nameLength) {
        return @()
    }
    $packageName = $segments[0..($nameLength - 1)] -join '/'
    $subpath = if ($segments.Count -gt $nameLength) {
        $segments[$nameLength..($segments.Count - 1)] -join '/'
    }
    else {
        ''
    }

    $packageDirectory = $null
    $directory = $importerDirectory
    while ($null -ne $directory -and (Test-UnderApplication -Path $directory)) {
        if ([IO.Path]::GetFileName($directory) -ine 'node_modules') {
            $candidate = Join-Path (Join-Path $directory 'node_modules') $packageName
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $packageDirectory = $candidate
                break
            }
        }
        $directory = [IO.Path]::GetDirectoryName($directory)
    }
    if ($null -eq $packageDirectory) {
        # Node.js built-ins and missing optional dependencies both land here.
        return @()
    }

    $results = [Collections.Generic.List[string]]::new()
    $manifestPath = Join-Path $packageDirectory 'package.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $results.Add([IO.Path]::GetFullPath($manifestPath))
    }
    $manifest = Read-PackageManifest -Directory $packageDirectory
    $exports = Get-JsonProperty -Object $manifest -Name 'exports'
    $resolved = $null
    if ($null -ne $exports) {
        $key = if ($subpath) { "./$subpath" } else { '.' }
        $target = Resolve-PackageExports -Exports $exports -Key $key
        if ($target -is [string]) {
            $targetPath = [IO.Path]::GetFullPath((Join-Path $packageDirectory $target))
            if (
                (Test-UnderApplication -Path $targetPath) -and
                (Test-Path -LiteralPath $targetPath -PathType Leaf)
            ) {
                $resolved = $targetPath
            }
        }
    }
    elseif ($subpath) {
        $resolved = Resolve-FileCandidate -Path (Join-Path $packageDirectory $subpath)
    }
    else {
        $resolved = Resolve-FileCandidate -Path $packageDirectory
    }
    if ($null -ne $resolved) {
        $results.Add($resolved)
    }
    return $results.ToArray()
}

$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$ordered = [Collections.Generic.List[string]]::new()
$queue = [Collections.Generic.Queue[string]]::new()
$truncated = $false

function Add-StartupFile {
    param([Parameter(Mandatory)][string]$Path)

    if ($ordered.Count -ge $MaximumFiles) {
        $script:truncated = $true
        return
    }
    if ($seen.Add($Path)) {
        $ordered.Add($Path)
        $queue.Enqueue($Path)
    }
}

function Invoke-Walk {
    while ($queue.Count -gt 0) {
        $file = $queue.Dequeue()
        if (-not $parsedExtensions.Contains([IO.Path]::GetExtension($file))) {
            continue
        }
        $text = [IO.File]::ReadAllText($file)
        foreach ($match in $specifierPattern.Matches($text)) {
            foreach ($dependency in Resolve-Specifier -Importer $file -Specifier $match.Groups[2].Value) {
                Add-StartupFile -Path $dependency
            }
        }
    }
}

Add-StartupFile -Path ([IO.Path]::GetFullPath($entryPoint))
Invoke-Walk

# Bundled extensions are found by directory discovery rather than imports, so
# their index files are seeded after the entry point's closure.
$extensionsRoot = Join-Path $applicationRoot 'dist\extensions'
if (Test-Path -LiteralPath $extensionsRoot -PathType Container) {
    foreach ($extensionDirectory in Get-ChildItem -LiteralPath $extensionsRoot -Directory | Sort-Object Name) {
        foreach ($index in 'index.js', 'index.mjs') {
            $indexPath = Join-Path $extensionDirectory.FullName $index
            if (Test-Path -LiteralPath $indexPath -PathType Leaf) {
                Add-StartupFile -Path $indexPath
            }
        }
    }
    Invoke-Walk
}

if ($truncated) {
    Write-Warning (
        "The startup file list reached its $MaximumFiles-file limit; " +
        'files farther from openclaw.mjs were omitted.'
    )
}

foreach ($path in $ordered) {
    [IO.Path]::GetRelativePath($applicationRoot, $path).Replace('\', '/')
}
