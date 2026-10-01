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

function Write-PathList {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Content,

        [string]$Name = 'paths.bin'
    )

    $path = Join-Path $testRoot $Name
    [IO.File]::WriteAllText(
        $path,
        $Content,
        [Text.UTF8Encoding]::new($false)
    )
    $path
}

function Assert-Relevance {
    param(
        [Parameter(Mandatory)]
        [object[]]$Files,

        [Parameter(Mandatory)]
        [ValidateSet('true', 'false')]
        [string]$Expected,

        [int]$MaximumFiles = 3000,

        [switch]$PayloadArtifact
    )

    $path = Write-FileList -Files $Files
    $parameters = @{
        FileListPath = $path
        MaximumFiles = $MaximumFiles
        PayloadArtifact = $PayloadArtifact
    }
    $actual = & $scriptPath @parameters
    if ($actual -cne $Expected) {
        throw "Expected packaging relevance $Expected; received $actual."
    }
}

function Assert-PathRelevance {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Content,

        [Parameter(Mandatory)]
        [ValidateSet('true', 'false')]
        [string]$Expected,

        [switch]$PayloadArtifact
    )

    $path = Write-PathList -Content $Content
    $actual = & $scriptPath `
        -PathListPath $path `
        -PayloadArtifact:$PayloadArtifact
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
    foreach ($surface in @('.github/workflows/gateway-msix-build.yml', 'scripts/Select-WorkflowBuild.ps1')) {
        Assert-Relevance -Expected true -Files @(@{ filename = $surface })
    }
    Assert-Relevance -Expected true -Files @(
        @{
            filename = 'docs/removed-script.md'
            previous_filename = 'scripts/Build-MSIX.ps1'
        }
    )
    Assert-Relevance -PayloadArtifact -Expected false -Files @(
        @{ filename = 'src/OpenClaw.Launcher/Program.cs' }
        @{ filename = 'scripts/Build-MSIX.ps1' }
        @{ filename = 'docs/local-development.md' }
    )
    foreach ($payloadPath in @(
        '.github/workflows/gateway-msix.yml'
        '.github/workflows/gateway-msix-build.yml'
        'release-policy.json'
        'scripts/Build-Payload.ps1'
        'scripts/Copy-PayloadTree.ps1'
        'scripts/Get-WorkflowSource.ps1'
        'scripts/OpenClawSource.ps1'
        'plugins/gateway-isolation/index.js'
    )) {
        Assert-Relevance -PayloadArtifact -Expected true -Files @(
            @{ filename = $payloadPath }
        )
    }
    Assert-Relevance -PayloadArtifact -Expected true -Files @(
        @{
            filename = 'docs/renamed.md'
            previous_filename = 'plugins/gateway-isolation/openclaw.plugin.json'
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
    Assert-Relevance `
        -PayloadArtifact `
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

    Assert-PathRelevance `
        -Expected false `
        -Content "README.md$([char]0)docs/a/b.txt$([char]0)LICENSE"
    Assert-PathRelevance `
        -Expected true `
        -Content "README.md$([char]0)src/App.cs$([char]0)docs/setup.md"
    Assert-PathRelevance `
        -Expected false `
        -Content "README.md$([char]0)$([char]0)docs/x.md$([char]0)"
    Assert-PathRelevance -Expected false -Content ''
    Assert-PathRelevance -Expected true -Content "src/readme.md.cs$([char]0)"
    Assert-PathRelevance `
        -PayloadArtifact `
        -Expected false `
        -Content "src/App.cs$([char]0)scripts/Build-MSIX.ps1$([char]0)"
    Assert-PathRelevance `
        -PayloadArtifact `
        -Expected true `
        -Content "src/App.cs$([char]0)plugins/gateway-isolation/index.js$([char]0)"

    Assert-Fails -MessagePattern 'does not exist' -Action {
        & $scriptPath -PathListPath (Join-Path $testRoot 'missing-paths.bin')
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Packaging relevance tests passed.'
