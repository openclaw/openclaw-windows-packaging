[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Get-PackagingRelevance.ps1'
$testRoot = Join-Path $env:TEMP (
    "openclaw-packaging-relevance-$([guid]::NewGuid().ToString('N'))"
)

function Write-FileList {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Files,

        [string]$Name = 'files.json'
    )

    $path = Join-Path $testRoot $Name
    ,$Files |
        ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath $path -Encoding utf8
    $path
}

function Assert-Relevance {
    param(
        [Parameter(Mandatory)]
        [object[]]$Files,

        [Parameter(Mandatory)]
        [ValidateSet('true', 'false')]
        [string]$Expected,

        [int]$MaximumFiles = 3000
    )

    $path = Write-FileList -Files $Files
    $actual = & $scriptPath `
        -FileListPath $path `
        -MaximumFiles $MaximumFiles
    if ($actual -cne $Expected) {
        throw "Expected packaging relevance $Expected; received $actual."
    }
}

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
            throw (
                "Expected failure matching '$MessagePattern'; received: " +
                $_.Exception.Message
            )
        }
        return
    }
    throw "Expected failure matching '$MessagePattern', but the action succeeded."
}

New-Item -Path $testRoot -ItemType Directory | Out-Null
try {
    Assert-Relevance -Expected false -Files @(
        @{ filename = 'README.md' }
        @{ filename = 'docs/setup.md' }
        @{ filename = 'LICENSE' }
    )
    Assert-Relevance -Expected true -Files @(
        @{ filename = 'README.md' }
        @{ filename = 'scripts/Build-MSIX.ps1' }
    )
    Assert-Relevance -Expected true -Files @(
        @{
            filename = 'docs/removed-script.md'
            previous_filename = 'scripts/Build-MSIX.ps1'
        }
    )

    $cappedFiles = @(
        for ($index = 0; $index -lt 3; $index++) {
            @{ filename = "docs/$index.md" }
        }
    )
    Assert-Relevance `
        -Expected true `
        -Files $cappedFiles `
        -MaximumFiles 3

    $emptyPath = Write-FileList -Files @() -Name 'empty.json'
    Assert-Fails -MessagePattern 'empty' -Action {
        & $scriptPath -FileListPath $emptyPath
    }

    $malformedPath = Join-Path $testRoot 'malformed.json'
    Set-Content -LiteralPath $malformedPath -Value '{'
    Assert-Fails -MessagePattern 'Unable to parse' -Action {
        & $scriptPath -FileListPath $malformedPath
    }

    Assert-Fails -MessagePattern 'does not exist' -Action {
        & $scriptPath -FileListPath (Join-Path $testRoot 'missing.json')
    }

    $invalidPath = Write-FileList `
        -Files @(@{ status = 'modified' }) `
        -Name 'invalid.json'
    Assert-Fails -MessagePattern 'invalid entry' -Action {
        & $scriptPath -FileListPath $invalidPath
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Packaging relevance tests passed.'
