[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FileListPath,

    [ValidateRange(1, 3000)]
    [int]$MaximumFiles = 3000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
        if ($path -notmatch '^(?:.*\.md|docs/.*|LICENSE)$') {
            return 'true'
        }
    }
}

'false'
