[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$RunId,
    [ValidateSet('auto', 'production', 'development')][string]$Channel = 'production',
    [scriptblock]$ApiInvoker,
    [scriptblock]$DownloadInvoker
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Channel = $Channel.ToLowerInvariant()
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
$selected = @($artifacts | Where-Object name -CEQ "openclaw-gateway-msix-store-submission-$($run.run_attempt)")
if ($selected.Count -eq 0) { return $null }
if ($selected.Count -ne 1 -or $selected[0].expired) { throw 'Store bundle artifact is ambiguous or expired.' }
$witnessName = "store-release-source-$($run.run_attempt)"
$witnessArtifacts = @($artifacts | Where-Object name -CEQ $witnessName)
if ($witnessArtifacts.Count -ne 1 -or $witnessArtifacts[0].expired) {
    throw 'The release run has no current, unexpired Store release witness.'
}
$directory = Join-Path ([IO.Path]::GetTempPath()) "store-source-$([guid]::NewGuid().ToString('N'))"
New-Item $directory -ItemType Directory | Out-Null
try {
    & $DownloadInvoker $witnessName $directory | Out-Null
    $witness = Get-Content (Join-Path $directory 'store-source.json') -Raw | ConvertFrom-Json
    if ([string]$witness.runId -cne $RunId -or [int]$witness.runAttempt -ne [int]$run.run_attempt -or
        $witness.packagingCommit -cne $run.head_sha -or $witness.packagingCommit -cnotmatch '^[0-9a-f]{40}$' -or
        $witness.releaseChannel -cnotin @('stable', 'beta') -or
        $witness.bundleSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Store release witness does not match this run and attempt.'
    }
    $preview = $witness.releaseChannel -cne 'stable'
    $tagPattern = if ($preview) {
        '^v[0-9]{4}\.[0-9]+\.[0-9]+-' + $witness.releaseChannel + '\.[1-9][0-9]*-msix\.[0-9]$'
    } else { '^v[0-9]{4}\.[0-9]+\.[0-9]+(?:-[2-9])?-msix\.[0-9]$' }
    if ($witness.releaseTag -cnotmatch $tagPattern -or
        $witness.bundleName -cne "OpenClawGateway-$($witness.releaseTag.Substring(1))-store.msixbundle") {
        throw 'Store release witness contains an invalid release identity or bundle name.'
    }
    $selectedChannel = if ($preview) { 'development' } else { 'production' }
    if ($Channel -cne 'auto' -and $Channel -cne $selectedChannel) { return $null }
    if ($preview) {
        $release = @(& $ApiInvoker "releases/tags/$($witness.releaseTag)")[0]
        $commit = @(& $ApiInvoker "commits/$($witness.releaseTag)")[0]
        if ($release.draft -or -not $release.prerelease -or -not $release.published_at -or
            $release.tag_name -cne $witness.releaseTag -or $commit.sha -cne $run.head_sha) {
            throw 'Development delivery requires the published GitHub prerelease at this packaging commit.'
        }
    }
    $bundleName = [string]$witness.bundleName
    $bundleSha256 = [string]$witness.bundleSha256
} finally { Remove-Item $directory -Recurse -Force }
[pscustomobject]@{
    channel = $selectedChannel
    artifactId = [string]$selected[0].id
    bundleName = $bundleName
    bundleSha256 = $bundleSha256
}
