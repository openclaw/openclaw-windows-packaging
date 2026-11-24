[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EventPath,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$GitHubRef = $env:GITHUB_REF,
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$RequestAttempt = $env:GITHUB_RUN_ATTEMPT,
    [scriptblock]$CommandInvoker
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Repository -cne 'openclaw/openclaw-windows-packaging' -or $GitHubRef -cne 'refs/heads/main' -or
    $EventName -cne 'workflow_run' -or $RequestAttempt -cne '1') {
    throw 'Automatic Store requests require a first-attempt completion workflow on canonical main; recover uncertain requests manually.'
}
$event = Get-Content -LiteralPath $EventPath -Raw | ConvertFrom-Json
$run = $event.workflow_run
if ($event.action -cne 'completed' -or $run.status -cne 'completed' -or $run.conclusion -cne 'success' -or
    $run.event -cne 'workflow_dispatch' -or $run.head_branch -cne 'main' -or
    $run.head_repository.full_name -cne $Repository -or
    $run.path -cne '.github/workflows/gateway-msix.yml') { return }
$runId = [string]$run.id
$attempt = [int]$run.run_attempt
if ($runId -cnotmatch '^[1-9][0-9]*$' -or $attempt -lt 1) { throw 'Invalid completed build identity.' }
if ($null -eq $CommandInvoker) {
    $CommandInvoker = {
        param([string[]]$CommandArguments)
        $output = & gh @CommandArguments
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect completed Store source; no publication requested.' }
        $output
    }
}
$inventory = (& $CommandInvoker @('api', "repos/$Repository/actions/runs/$runId/artifacts?per_page=100")) |
    ConvertFrom-Json
if (@($inventory.artifacts).Count -ne [int]$inventory.total_count) { throw 'Incomplete Store witness inventory.' }
$name = "store-release-source-$attempt"
$witnesses = @($inventory.artifacts | Where-Object name -CEQ $name)
# Ordinary builds and older release workflows did not opt in to this handoff.
if ($witnesses.Count -eq 0) { return }
if ($witnesses.Count -ne 1 -or $witnesses[0].expired) { throw 'Missing unique retained Store witness.' }
$directory = Join-Path ([IO.Path]::GetTempPath()) "gateway-store-request-$([guid]::NewGuid().ToString('N'))"
[void][IO.Directory]::CreateDirectory($directory)
try {
    & $CommandInvoker @('run', 'download', $runId, '--repo', $Repository, '--name', $name, '--dir', $directory) | Out-Null
    $source = Get-Content -LiteralPath (Join-Path $directory 'store-source.json') -Raw | ConvertFrom-Json
    if ([string]$source.runId -cne $runId -or [int]$source.runAttempt -ne $attempt -or
        $source.packagingCommit -cne $run.head_sha) { throw 'Store witness belongs to another build or attempt.' }
    $optIn = $source.PSObject.Properties['automaticStorePublication']
    if ($null -eq $optIn -or $optIn.Value -isnot [bool] -or -not $optIn.Value) { return }
    if ($source.releaseChannel -cnotin @('stable', 'beta')) { throw 'Unsupported Store release channel.' }
    [pscustomobject]@{
        runId = $runId
        channel = if ($source.releaseChannel -ceq 'beta') { 'development' } else { 'production' }
    }
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
