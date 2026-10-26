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
    $global:StoreSourceTest_artifacts = @([pscustomobject]@{ id = 456; name = 'openclaw-gateway-msix-store-submission'; expired = $false })

}
$api = {
    param($Path)
    switch -Wildcard ($Path) {
        'actions/runs/123' { return $global:StoreSourceTest_run }
        'actions/runs/123/artifacts*' { return [pscustomobject]@{ artifacts = $global:StoreSourceTest_artifacts } }

        default { throw "Unexpected API path: $Path" }
    }
}
$download = {
    param($Name, $Directory)
    throw 'Packaging source selection must not download untrusted content.'
}
function Invoke-Source { & $scriptPath -RunId 123 -ApiInvoker $api -DownloadInvoker $download }
Reset-Fixture
$result = Invoke-Source
if ($result.artifactId -cne '456') { throw 'Authorized release selected the wrong artifact.' }
$script:checks++
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
