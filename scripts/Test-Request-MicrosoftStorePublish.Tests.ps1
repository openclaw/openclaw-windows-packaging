[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Request-MicrosoftStorePublish.ps1'
$script:checks = 0
$fixture = [pscustomobject]@{ calls = @() }
$capture = {
    param([string[]]$CommandArguments)
    $fixture.calls += ,$CommandArguments
}.GetNewClosure()
$canonical = @{
    RunId = '123'
    Repository = 'openclaw/openclaw-windows-packaging'
    GitHubRef = 'refs/heads/main'
    EventName = 'workflow_dispatch'
    CommandInvoker = $capture
}

function Assert-Rejected {
    param([hashtable]$Parameters)
    $fixture.calls = @()
    $rejected = $false
    try { & $scriptPath @Parameters | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected -or $fixture.calls.Count -ne 0) {
        throw 'An invalid request reached the external command boundary.'
    }
    $script:checks++
}

$result = & $scriptPath @canonical
$expected = @(
    'workflow', 'run', 'windows-store-publish.yml',
    '--repo', 'openclaw/releases', '--ref', 'main',
    '-f', 'product=gateway', '-f', 'run_id=123'
)
if ($fixture.calls.Count -ne 1 -or
    ($fixture.calls[0] | ConvertTo-Json -Compress) -cne ($expected | ConvertTo-Json -Compress)) {
    throw 'The request did not dispatch exactly the fixed central workflow and source run.'
}
if ($result.product -cne 'gateway' -or $result.sourceRunId -cne '123' -or $result.status -cne 'requested') {
    throw 'A request must report request acceptance, not Microsoft Store acceptance.'
}
$script:checks++

foreach ($runId in @('0', '-1', '01', '1.0', '1e2', ' 123', '123 ', "123`n", '123;command', '')) {
    $parameters = $canonical.Clone()
    $parameters.RunId = $runId
    Assert-Rejected $parameters
}
foreach ($override in @(
    @{ Repository = 'fork/openclaw-windows-packaging' },
    @{ GitHubRef = 'refs/heads/patch' },
    @{ GitHubRef = 'refs/tags/main' },
    @{ EventName = 'workflow_run' },
    @{ EventName = 'pull_request' }
)) {
    $parameters = $canonical.Clone()
    foreach ($key in $override.Keys) { $parameters[$key] = $override[$key] }
    Assert-Rejected $parameters
}

$parameters = $canonical.Clone()
$parameters.CommandInvoker = { throw 'Synthetic dispatch failure.' }
$failed = $false
try { & $scriptPath @parameters | Out-Null }
catch {
    if ($_.Exception.Message -cne 'Synthetic dispatch failure.') { throw }
    $failed = $true
}
if (-not $failed) { throw 'A failed dispatch was reported as accepted.' }
$script:checks++

Write-Host "Store publication requests: $script:checks scenarios passed."
