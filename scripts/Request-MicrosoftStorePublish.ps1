[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\A[1-9][0-9]*\z')][string]$RunId,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$GitHubRef = $env:GITHUB_REF,
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [scriptblock]$CommandInvoker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Repository -cne 'openclaw/openclaw-windows-packaging' -or
    $GitHubRef -cne 'refs/heads/main' -or
    $EventName -cne 'workflow_dispatch') {
    throw 'Store publication requests require a manual dispatch from the canonical main branch.'
}

if ($null -eq $CommandInvoker) {
    $CommandInvoker = {
        param([string[]]$CommandArguments)
        & gh @CommandArguments
        if ($LASTEXITCODE -ne 0) {
            throw 'Store publication request was not confirmed. Inspect openclaw/releases before retrying.'
        }
    }
}

$commandArguments = @(
    'workflow', 'run', 'windows-store-publish.yml',
    '--repo', 'openclaw/releases', '--ref', 'main',
    '-f', 'product=gateway', '-f', "run_id=$RunId"
)
& $CommandInvoker $commandArguments | Out-Null

[pscustomobject]@{
    product = 'gateway'
    sourceRunId = $RunId
    status = 'requested'
}
