[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$RunId,
    [scriptblock]$ApiInvoker,
    [scriptblock]$DownloadInvoker
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = 'openclaw/openclaw-windows-packaging'
if ($null -eq $ApiInvoker) {
    $ApiInvoker = {
        param($Path)
        $json = & gh api --paginate --slurp "repos/$repository/$Path"
        if ($LASTEXITCODE -ne 0) { throw 'Unable to read Store source provenance from GitHub.' }
        return @($json | ConvertFrom-Json)
    }
}
if ($null -eq $DownloadInvoker) {
    $DownloadInvoker = {
        param($Name, $Directory)
        & gh run download $RunId --repo $repository --name $Name --dir $Directory
        if ($LASTEXITCODE -ne 0) { throw 'Unable to download Store release witness.' }
    }
}
$run = @(& $ApiInvoker "actions/runs/$RunId")[0]

if ($run.path -cne '.github/workflows/gateway-msix.yml' -or
    $run.event -cne 'workflow_dispatch' -or $run.head_branch -cne 'main') { return $null }
if ($run.repository.full_name -cne $repository -or $run.head_repository.full_name -cne $repository -or
    [string]$run.id -cne $RunId -or $run.status -cne 'completed' -or $run.conclusion -cne 'success') {
    throw 'Store submission requires a successful completed run in the canonical repository.'
}
$pages = @(& $ApiInvoker "actions/runs/$RunId/artifacts?per_page=100")
$artifacts = @($pages | ForEach-Object { $_.artifacts })
$selected = @($artifacts | Where-Object name -CEQ 'openclaw-gateway-msix-store-submission')
if ($selected.Count -eq 0) { return $null }
if ($selected.Count -ne 1 -or $selected[0].expired) { throw 'Store bundle artifact is ambiguous or expired.' }
$bundleName = ''
$bundleSha256 = ''
[pscustomobject]@{
    artifactId = [string]$selected[0].id
    bundleName = $bundleName
    bundleSha256 = $bundleSha256
}
