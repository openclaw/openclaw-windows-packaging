<#
.SYNOPSIS
    Runs the repository's canonical .NET quality checks.

.DESCRIPTION
    This is the single entry point used by local development, continuous
    integration, and the optional pre-push hook, so every surface reports the
    same result.

    The script performs a full restore and a Release rebuild with the analyzer
    policy from Directory.Build.props and the root .editorconfig. A rebuild is
    required because an up-to-date project is skipped and reports no analyzer
    diagnostics at all.

    Analyzer severity is configured in .editorconfig rather than here. While
    rules are still being cleared they report as warnings and this script
    succeeds; once a rule family is promoted to error the same command fails.

.PARAMETER Configuration
    The MSBuild configuration to rebuild. Defaults to Release, which matches
    continuous integration.

.PARAMETER SkipRestore
    Reuse the existing restore output instead of restoring first. Use this only
    when a restore has already run against the current dependency manifests.

.EXAMPLE
    .\scripts\Test-DotNetQuality.ps1

.EXAMPLE
    .\scripts\Test-DotNetQuality.ps1 -SkipRestore
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',

    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $repositoryRoot 'OpenClaw.Gateway.MSIX.slnx'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $solution)) {
    throw "Could not find the solution at '$solution'."
}

if (-not $SkipRestore) {
    Write-Host 'Restoring packages...'
    Invoke-CheckedCommand `
        -Command { dotnet restore $solution } `
        -FailureMessage 'Package restore failed.'
}

Write-Host "Rebuilding with static analysis ($Configuration)..."
Invoke-CheckedCommand `
    -Command {
        dotnet build $solution `
            --configuration $Configuration `
            --no-restore `
            -t:Rebuild
    } `
    -FailureMessage 'Static analysis build failed.'

Write-Host 'Static analysis completed successfully.'
