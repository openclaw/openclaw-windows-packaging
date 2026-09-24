[CmdletBinding(DefaultParameterSetName = 'FileList')]
param(
    [Parameter(Mandatory, ParameterSetName = 'FileList')]
    [string]$FileListPath,

    [Parameter(ParameterSetName = 'FileList')]
    [ValidateRange(1, 3000)]
    [int]$MaximumFiles = 3000,

    [Parameter(Mandatory, ParameterSetName = 'PathList')]
    [string]$PathListPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# FileList preserves CI's GitHub JSON contract; PathList accepts local git -z output.
$documentationPathPattern = '^(?:.*\.md|docs/.*|LICENSE)$'

if ($PSCmdlet.ParameterSetName -eq 'PathList') {
    if (-not (Test-Path -LiteralPath $PathListPath -PathType Leaf)) {
        throw "Path list does not exist: $PathListPath"
    }

    $paths = @(
        [IO.File]::ReadAllText(
            $PathListPath,
            [Text.UTF8Encoding]::new($false)
        ).Split([char]0) |
            Where-Object { $_.Length -gt 0 }
    )
    foreach ($path in $paths) {
        if ($path -notmatch $documentationPathPattern) {
            return 'true'
        }
    }

    return 'false'
}

if (-not (Test-Path -LiteralPath $FileListPath -PathType Leaf)) {
    throw "Pull request file list does not exist: $FileListPath"
}

try {
    $pages = @(
        Get-Content -LiteralPath $FileListPath -Raw |
            ConvertFrom-Json
    )
}
catch {
    throw "Unable to parse the pull request file list: $($_.Exception.Message)"
}

$files = @(
    foreach ($page in $pages) {
        foreach ($file in @($page)) {
            if ($null -eq $file -or
                $file.PSObject.Properties.Name -notcontains 'filename' -or
                [string]::IsNullOrWhiteSpace([string]$file.filename)) {
                throw 'The pull request file list contains an invalid entry.'
            }
            $file
        }
    }
)
if ($files.Count -eq 0) {
    throw 'The pull request file list was empty.'
}

# GitHub returns at most 3,000 files. At the cap there is no proof that the
# response is complete, so conservatively run packaging.
if ($files.Count -ge $MaximumFiles) {
    return 'true'
}

foreach ($file in $files) {
    $previousFilename = if (
        $file.PSObject.Properties.Name -contains 'previous_filename'
    ) {
        [string]$file.previous_filename
    }
    else {
        ''
    }
    foreach ($path in @(
        [string]$file.filename
        $previousFilename
    )) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }
        if ($path -notmatch $documentationPathPattern) {
            return 'true'
        }
    }
}

'false'
