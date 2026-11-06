[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Get-StoreSubmissionSource.ps1'
$repository = 'openclaw/openclaw-windows-packaging'
$script:checks = 0
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $script:checks++
        return
    }
    throw "Expected rejection: $Pattern"
}
function Reset-Fixture {
    $global:StoreSourceTest_run = [pscustomobject]@{
        id = 123; status = 'completed'; conclusion = 'success'; run_attempt = 2
        repository = [pscustomobject]@{ full_name = $repository }
        head_repository = [pscustomobject]@{ full_name = $repository }
        path = '.github/workflows/gateway-msix.yml'; event = 'workflow_dispatch'; head_branch = 'main'; head_sha = ('a' * 40)
    }
    $global:StoreSourceTest_artifacts = @(
        [pscustomobject]@{ id = 456; name = 'openclaw-gateway-msix-store-submission-2'; expired = $false }
        [pscustomobject]@{ id = 789; name = 'store-release-source-2'; expired = $false }
    )
    $global:StoreSourceTest_witness = [ordered]@{
        runId = '123'; runAttempt = 2; packagingCommit = ('a' * 40)
        releaseTag = 'v2026.11.1-msix.0'; releaseChannel = 'stable'
        bundleName = 'OpenClawGateway-2026.11.1-msix.0-store.msixbundle'; bundleSha256 = ('b' * 64)
    }
    $global:StoreSourceTest_release = [pscustomobject]@{
        draft = $false; prerelease = $true; published_at = '2026-11-05'
        tag_name = 'v2026.11.1-beta.1-msix.0'
    }
    $global:StoreSourceTest_commit = [pscustomobject]@{ sha = ('a' * 40) }
}
$api = {
    param($Path)
    switch -Wildcard ($Path) {
        'actions/runs/123' { return $global:StoreSourceTest_run }
        'actions/runs/123/artifacts*' { return [pscustomobject]@{ artifacts = $global:StoreSourceTest_artifacts } }
        'releases/tags/*' { return $global:StoreSourceTest_release }
        'commits/*' { return $global:StoreSourceTest_commit }
        default { throw "Unexpected API path: $Path" }
    }
}
$download = {
    param($Name, $Directory)
    if ($Name -cne 'store-release-source-2') { throw 'Unexpected witness download.' }
    [IO.File]::WriteAllText((Join-Path $Directory 'store-source.json'), ($global:StoreSourceTest_witness | ConvertTo-Json))
}
function Invoke-Source([string]$Channel = 'auto') { & $scriptPath -RunId 123 -Channel $Channel -ApiInvoker $api -DownloadInvoker $download }
Reset-Fixture
$result = Invoke-Source
if ($result.artifactId -cne '456') { throw 'Authorized release selected the wrong artifact.' }
$script:checks++

foreach ($channel in @('beta')) {
    Reset-Fixture
    $global:StoreSourceTest_witness.releaseChannel = $channel
    $global:StoreSourceTest_witness.releaseTag = "v2026.11.1-$channel.1-msix.0"
    $global:StoreSourceTest_witness.bundleName = "OpenClawGateway-2026.11.1-$channel.1-msix.0-store.msixbundle"
    $global:StoreSourceTest_release.tag_name = $global:StoreSourceTest_witness.releaseTag
    $result = Invoke-Source
    if ($result.channel -cne 'development' -or $result.bundleSha256 -cne ('b' * 64)) { throw 'Preview destination or hash drifted.' }
    if ($null -ne (Invoke-Source production)) { throw 'Preview reached production.' }
    if ((Invoke-Source development).channel -cne 'development') { throw 'Manual development selection failed.' }
    foreach ($selection in @('Auto', 'AUTO', 'Development', 'DEVELOPMENT')) {
        if ((Invoke-Source $selection).channel -cne 'development') { throw 'Case-variant preview selection failed.' }
        $script:checks++
    }
    $script:checks += 3
    $global:StoreSourceTest_release.prerelease = $false
    Assert-Fails { Invoke-Source } 'published GitHub prerelease'
    $global:StoreSourceTest_release.prerelease = $true
    $global:StoreSourceTest_commit.sha = ('c' * 40)
    Assert-Fails { Invoke-Source } 'published GitHub prerelease'
}
Reset-Fixture
if ($null -ne (Invoke-Source development)) { throw 'Stable release reached Development.' }
$script:checks++
foreach ($field in @('runId', 'runAttempt', 'packagingCommit', 'releaseChannel', 'bundleSha256')) {
    Reset-Fixture
    $global:StoreSourceTest_witness[$field] = if ($field -eq 'runAttempt') { 1 } else { 'invalid' }
    Assert-Fails { Invoke-Source } 'does not match'
}
foreach ($field in @('releaseTag', 'bundleName')) {
    Reset-Fixture
    $global:StoreSourceTest_witness[$field] = '../invalid'
    Assert-Fails { Invoke-Source } 'invalid release identity'
}
Reset-Fixture
$global:StoreSourceTest_artifacts[1].expired = $true
Assert-Fails { Invoke-Source } 'Store release witness'
Reset-Fixture
$global:StoreSourceTest_artifacts = @($global:StoreSourceTest_artifacts[0])
Assert-Fails { Invoke-Source } 'Store release witness'
foreach ($field in @('conclusion', 'status')) {
    Reset-Fixture
    $global:StoreSourceTest_run.$field = 'failure'
    Assert-Fails { Invoke-Source } 'successful completed run'
}
Reset-Fixture
$global:StoreSourceTest_run.head_repository.full_name = 'fork/untrusted'
Assert-Fails { Invoke-Source } 'canonical repository'
Reset-Fixture
$global:StoreSourceTest_run.event = 'pull_request'
$global:StoreSourceTest_run.head_repository.full_name = 'fork/untrusted'
if ($null -ne (Invoke-Source)) { throw 'A fork PR was not skipped.' }
$script:checks++
foreach ($field in @('path', 'event', 'head_branch')) {
    Reset-Fixture
    $global:StoreSourceTest_run.$field = 'pull_request'
    if ($null -ne (Invoke-Source)) { throw "Untrusted $field was eligible." }
    $script:checks++
}
Reset-Fixture
$global:StoreSourceTest_artifacts[0].expired = $true
Assert-Fails { Invoke-Source } 'ambiguous or expired'
Reset-Fixture
$global:StoreSourceTest_artifacts += $global:StoreSourceTest_artifacts[0]
Assert-Fails { Invoke-Source } 'ambiguous or expired'
Reset-Fixture
$global:StoreSourceTest_artifacts = @()
if ($null -ne (Invoke-Source)) { throw 'A run without an authorized bundle was eligible.' }
$script:checks++

Write-Host "Store source authorization: $script:checks scenarios passed."

Get-Variable -Name StoreSourceTest_* -Scope Global | Remove-Variable -Scope Global
