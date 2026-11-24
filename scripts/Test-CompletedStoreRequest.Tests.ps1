[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Get-StorePublicationRequest.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "completed-store-test-$([guid]::NewGuid().ToString('N'))"
[void][IO.Directory]::CreateDirectory($root)
$script:checks = 0

function Invoke-Scenario {
    param([hashtable]$Run = @{}, [hashtable]$Source = @{}, [hashtable]$Inventory = @{},
        [hashtable]$Context = @{}, [hashtable]$CurrentRun = @{}, [string]$Ancestry = 'ahead', [switch]$Legacy, [switch]$FailDownload,
        [string]$ExpectedChannel = '', [switch]$Rejected, [switch]$NoRead)
    $event = @{ action = 'completed'; workflow_run = @{
        id = 123; run_attempt = 1; head_sha = ('a' * 40); status = 'completed'; conclusion = 'success'
        event = 'workflow_dispatch'; head_branch = 'main'; head_repository = @{ full_name = 'openclaw/openclaw-windows-packaging' }
        path = '.github/workflows/gateway-msix.yml'
    } }
    foreach ($key in $Run.Keys) { $event.workflow_run[$key] = $Run[$key] }
    $current = $event.workflow_run.Clone()
    foreach ($key in $CurrentRun.Keys) { $current[$key] = $CurrentRun[$key] }
    $witness = @{ runId = '123'; runAttempt = 1; packagingCommit = ('a' * 40); releaseChannel = 'stable'; automaticStorePublication = $true }
    foreach ($key in $Source.Keys) { $witness[$key] = $Source[$key] }
    if ($Legacy) { $witness.Remove('automaticStorePublication') }
    $inventoryResult = @{ total_count = 1; artifacts = @(@{ name = 'store-release-source-1'; expired = $false }) }
    foreach ($key in $Inventory.Keys) { $inventoryResult[$key] = $Inventory[$key] }
    $fixture = @{ calls = @(); downloadPath = $null }
    $invoker = {
        param([string[]]$CommandArguments)
        $fixture.calls += ,$CommandArguments
        if ($CommandArguments[0] -ceq 'api') {
            if ($CommandArguments[1] -ceq "repos/openclaw/openclaw-windows-packaging/compare/$('a' * 40)...main") {
                return (@{ status = $Ancestry } | ConvertTo-Json)
            }
            if ($CommandArguments[1] -ceq 'repos/openclaw/openclaw-windows-packaging/actions/runs/123') {
                return ($current | ConvertTo-Json -Depth 10)
            }
            if ($CommandArguments[1] -cne 'repos/openclaw/openclaw-windows-packaging/actions/runs/123/artifacts?per_page=100') {
                throw 'Unexpected inventory request.'
            }
            return ($inventoryResult | ConvertTo-Json -Depth 10)
        }
        $expected = @('run', 'download', '123', '--repo', 'openclaw/openclaw-windows-packaging', '--name', 'store-release-source-1', '--dir')
        if (($CommandArguments[0..7] -join '|') -cne ($expected -join '|')) { throw 'Unexpected download request.' }
        $fixture.downloadPath = $CommandArguments[8]
        if ($FailDownload) { throw 'Synthetic artifact failure.' }
        [IO.File]::WriteAllText((Join-Path $fixture.downloadPath 'store-source.json'), ($witness | ConvertTo-Json))
    }.GetNewClosure()
    $parameters = @{
        EventPath = (Join-Path $root 'event.json'); Repository = 'openclaw/openclaw-windows-packaging'
        GitHubRef = 'refs/heads/main'; EventName = 'workflow_run'; RequestAttempt = '1'; CommandInvoker = $invoker
    }
    foreach ($key in $Context.Keys) { $parameters[$key] = $Context[$key] }
    [IO.File]::WriteAllText($parameters.EventPath, ($event | ConvertTo-Json -Depth 10))
    $failure = $null
    $result = $null
    try { $result = & $scriptPath @parameters } catch { $failure = $_ }
    if ($Rejected) {
        if ($null -eq $failure) { throw 'Invalid completion was accepted.' }
    } elseif ($null -ne $failure) { throw $failure }
    elseif ($ExpectedChannel) {
        if ($result.runId -cne '123' -or $result.channel -cne $ExpectedChannel) { throw 'Wrong completed candidate selected.' }
    } elseif ($null -ne $result) { throw 'Ineligible completion requested publication.' }
    if ($NoRead -and $fixture.calls.Count -ne 0) { throw 'Ineligible event reached artifact reads.' }
    if ($null -ne $fixture.downloadPath -and (Test-Path -LiteralPath $fixture.downloadPath)) { throw 'Temporary source was not cleaned up.' }
    $script:checks++
}

try {
    Invoke-Scenario -ExpectedChannel production
    Invoke-Scenario -Source @{ releaseChannel = 'beta' } -ExpectedChannel development
    Invoke-Scenario -Ancestry identical -ExpectedChannel production
    foreach ($ancestry in @('behind', 'diverged', 'unknown')) { Invoke-Scenario -Ancestry $ancestry -Rejected }
    Invoke-Scenario -Context @{ EventName = 'workflow_dispatch'; RunId = '123'; Channel = 'production' } -ExpectedChannel production -NoRead
    Invoke-Scenario -Context @{ EventName = 'workflow_dispatch'; RunId = '123'; Channel = 'development' } -ExpectedChannel development -NoRead
    foreach ($invalid in @('01', "123`nrun_id=456", '123;command', ' 123')) {
        Invoke-Scenario -Context @{ EventName = 'workflow_dispatch'; RunId = $invalid; Channel = 'production' } -Rejected -NoRead
    }
    foreach ($current in @(@{ run_attempt = 2 }, @{ status = 'in_progress' }, @{ conclusion = 'failure' }, @{ head_sha = ('b' * 40) })) {
        Invoke-Scenario -CurrentRun $current -Rejected
    }
    # Completion, not elapsed build time, is the prerequisite for dispatch.
    foreach ($run in @(
        @{ status = 'in_progress' }, @{ conclusion = 'failure' }, @{ event = 'pull_request' },
        @{ head_branch = 'feature' }, @{ head_repository = @{ full_name = 'fork/packaging' } },
        @{ path = '.github/workflows/other.yml' }
    )) { Invoke-Scenario -Run $run -NoRead }
    foreach ($context in @(
        @{ Repository = 'fork/packaging' }, @{ GitHubRef = 'refs/heads/feature' },
        @{ EventName = 'workflow_dispatch' }, @{ RequestAttempt = '2' }
    )) { Invoke-Scenario -Context $context -Rejected -NoRead }
    Invoke-Scenario -Legacy
    Invoke-Scenario -Source @{ automaticStorePublication = $false }
    Invoke-Scenario -Source @{ automaticStorePublication = 'true' }
    Invoke-Scenario -Inventory @{ total_count = 0; artifacts = @() }
    Invoke-Scenario -Inventory @{ total_count = 2 } -Rejected
    Invoke-Scenario -Inventory @{ artifacts = @(@{ name = 'store-release-source-1'; expired = $true }) } -Rejected
    Invoke-Scenario -Inventory @{ total_count = 2; artifacts = @(@{ name = 'store-release-source-1'; expired = $false }, @{ name = 'store-release-source-1'; expired = $false }) } -Rejected
    Invoke-Scenario -Inventory @{ artifacts = @(@{ name = 'store-release-source-2'; expired = $false }) }
    foreach ($source in @(
        @{ runId = '124' }, @{ runAttempt = 2 }, @{ packagingCommit = ('b' * 40) }, @{ releaseChannel = 'nightly' }
    )) { Invoke-Scenario -Source $source -Rejected }
    Invoke-Scenario -FailDownload -Rejected
    Write-Host "Completed Store requests: $script:checks scenarios passed."
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
