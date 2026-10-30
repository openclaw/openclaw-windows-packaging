[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Get-WorkflowSourceSnapshotMode.ps1'
$jobName = 'Capture source and validate build route'
$saveName = 'Save immutable source selection'
$script:checks = 0

function New-Step([string]$Name, [string]$Conclusion, [string]$Status = 'completed') {
    [pscustomobject]@{ name = $Name; status = $Status; conclusion = $Conclusion }
}
function New-Job([string]$Conclusion, [object[]]$Steps, [string]$Status = 'completed', [string]$Name = $jobName) {
    [pscustomobject]@{ name = $Name; status = $Status; conclusion = $Conclusion; steps = @($Steps) }
}
function New-CapturedJob([string]$SaveConclusion, [string]$JobConclusion) {
    New-Job $JobConclusion @(
        (New-Step 'Set up job' 'success')
        (New-Step 'Resolve immutable OpenClaw source' $(if ($SaveConclusion -ceq 'skipped') { 'failure' } else { 'success' }))
        (New-Step $saveName $SaveConclusion)
    )
}
$running = New-Job $null @() -Status 'in_progress'

function Invoke-Mode {
    param(
        [int]$Attempt,
        [hashtable]$Jobs = @{},
        [object[]]$Artifacts = @(),
        [string]$Fail = ''
    )
    $global:SnapshotModeTest_calls = [Collections.Generic.List[string]]::new()
    $api = {
        param($Path)
        $global:SnapshotModeTest_calls.Add($Path)
        if ($Fail -ne '' -and $Path -like $Fail) { throw "GitHub API unavailable: $Path" }
        if ($Path -match '\Aactions/runs/77/attempts/(?<n>[0-9]+)/jobs\?per_page=100\z') {
            $n = [int]$Matches.n
            $list = if ($Jobs.ContainsKey($n)) { @($Jobs[$n]) } else { @() }
            return , [pscustomobject]@{ total_count = $list.Count; jobs = $list }
        }
        if ($Path -ceq 'actions/runs/77/artifacts?name=openclaw-source-resolution&per_page=100') {
            return , [pscustomobject]@{ total_count = $Artifacts.Count; artifacts = @($Artifacts) }
        }
        throw "Unexpected API path: $Path"
    }.GetNewClosure()
    & $scriptPath -Repository 'openclaw/openclaw-windows-packaging' -RunId 77 -RunAttempt $Attempt `
        -PreflightJobName $jobName -SaveStepName $saveName -ApiInvoker $api
}
function Assert-Mode([string]$Expected, [string]$Case, [scriptblock]$Action) {
    $actual = & $Action
    if ($actual -cne $Expected) { throw "$Case`: expected '$Expected', got '$actual'." }
    $script:checks++
}
function Assert-Fails([string]$Pattern, [string]$Case, [scriptblock]$Action) {
    try { $null = & $Action } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $script:checks++
        return
    }
    throw "$Case`: expected a failure matching '$Pattern'."
}
$artifact = [pscustomobject]@{ id = 9; name = 'openclaw-source-resolution'; expired = $false }

Assert-Mode 'resolve' 'first attempt' { Invoke-Mode 1 }
if ($global:SnapshotModeTest_calls.Count -ne 0) { throw 'The first attempt must resolve without reading run state.' }
$script:checks++

# Run 41113043368 (#219): a fork pull request run waited for approval, so
# attempt 1 completed as action_required with no jobs and no artifact.
Assert-Mode 'resolve' 'approved fork run after an approval-gated attempt' {
    Invoke-Mode 2 -Jobs @{ 1 = @(); 2 = $running }
}

Assert-Mode 'reuse' 'rerun after a captured attempt' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-CapturedJob 'success' 'failure'); 2 = $running } -Artifacts $artifact
}
Assert-Mode 'reuse' 'rerun with an expired snapshot replays and fails closed on download' {
    $expired = [pscustomobject]@{ id = 9; name = 'openclaw-source-resolution'; expired = $true }
    Invoke-Mode 2 -Jobs @{ 1 = (New-CapturedJob 'success' 'success'); 2 = $running } -Artifacts $expired
}
Assert-Mode 'reuse' 'missing snapshot after a successful capture job' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-CapturedJob 'success' 'success'); 2 = $running }
}
Assert-Mode 'reuse' 'missing snapshot after an upload step that ran but failed' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-CapturedJob 'failure' 'failure'); 2 = $running }
}
Assert-Mode 'reuse' 'cancelled capture job during upload' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-CapturedJob 'cancelled' 'cancelled'); 2 = $running }
}
Assert-Mode 'reuse' 'earlier capture job without a recognizable upload step' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-Job 'failure' @(New-Step 'Set up job' 'success')); 2 = $running }
}
Assert-Mode 'reuse' 'earlier capture job with an unfinished upload step' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-Job 'failure' @(New-Step $saveName $null -Status 'in_progress')); 2 = $running }
}
Assert-Mode 'reuse' 'earlier capture job that never reported completion' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-Job $null @() -Status 'in_progress'); 2 = $running }
}
Assert-Mode 'resolve' 'earlier capture failed before upload' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-CapturedJob 'skipped' 'failure'); 2 = $running }
}
Assert-Mode 'resolve' 'earlier capture job skipped' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-Job 'skipped' @()); 2 = $running }
}
Assert-Mode 'resolve' 'earlier capture job ended before any step ran' {
    Invoke-Mode 2 -Jobs @{ 1 = (New-Job 'failure' @()); 2 = $running }
}
Assert-Mode 'resolve' 'other jobs in an earlier attempt do not count as captures' {
    $other = New-Job 'success' @(New-Step $saveName 'success') -Name 'Test Gateway MSIX host'
    Invoke-Mode 2 -Jobs @{ 1 = $other; 2 = $running }
}
Assert-Mode 'reuse' 'rerun after an approved attempt captured the snapshot' {
    Invoke-Mode 3 -Jobs @{ 1 = @(); 2 = (New-CapturedJob 'success' 'failure'); 3 = $running } -Artifacts $artifact
}
Assert-Mode 'reuse' 'missing snapshot after an approved attempt captured it' {
    Invoke-Mode 3 -Jobs @{ 1 = @(); 2 = (New-CapturedJob 'success' 'success'); 3 = $running }
}
Assert-Fails "does not list the job '$jobName'" 'drifted job name' {
    Invoke-Mode 2 -Jobs @{ 1 = @(); 2 = (New-Job $null @() -Status 'in_progress' -Name 'Renamed job') }
}
Assert-Fails 'GitHub API unavailable' 'unreadable artifacts' {
    Invoke-Mode 2 -Jobs @{ 1 = @(); 2 = $running } -Fail 'actions/runs/77/artifacts*'
}
Assert-Fails 'GitHub API unavailable' 'unreadable earlier attempt' {
    Invoke-Mode 2 -Jobs @{ 1 = @(); 2 = $running } -Fail 'actions/runs/77/attempts/1/*'
}

Remove-Variable -Name SnapshotModeTest_calls -Scope Global -ErrorAction SilentlyContinue
Write-Host "Workflow source snapshot mode tests passed ($script:checks checks)."
