[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Get-WorkflowSource.ps1'
$policyPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'release-policy.json'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw-workflow-source-$([guid]::NewGuid().ToString('N'))")
$snapshotPath = Join-Path $testRoot 'source-resolution.json'
$packagingCommit = '1' * 40
$version = '2026.9.4'
$source = [ordered]@{
    repository = $policy.repository
    requestedRef = $policy.channel
    resolvedCommit = '2' * 40
    packageVersion = $version
    channel = $policy.channel
    releaseTag = "v$version"
    tagObject = '3' * 40
    resolvedAt = '2026-09-15T00:00:00.0000000Z'
    registryIntegrity = 'sha512-' + [Convert]::ToBase64String([byte[]]::new(64))
    packagingCommit = $packagingCommit
    workflowRunId = '12345'
    workflowRunNumber = 42
    signingMode = 'official'
    msixPackageVersion = "$version.$($policy.packageRevision)"
    msixReleaseTag = "v$version.$($policy.packageRevision)"
}
$parameters = @{
    PolicyPath = $policyPath
    OutputPath = $snapshotPath
    SigningMode = 'official'
    RunNumber = 42
    WorkflowRunId = '12345'
    PackagingCommit = $packagingCommit
}

$requests = [Collections.Generic.List[string]]::new()
$responses = @{}
function Invoke-RestMethod {
    param($Uri, $Headers, $TimeoutSec, $OperationTimeoutSeconds, $MaximumRedirection, $ErrorAction)
    if (-not $responses.ContainsKey([string]$Uri)) {
        throw "Unexpected network request in workflow snapshot test: $Uri"
    }
    $requests.Add([string]$Uri)
    return $responses[[string]$Uri]
}

function Assert-Fails {
    param([scriptblock]$Action, [string]$MessagePattern)
    try {
        & $Action | Out-Null
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw
        }
        return
    }
    throw "Expected failure matching '$MessagePattern'."
}

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Assert-Fails -MessagePattern 'snapshot is unavailable' -Action {
        & $scriptPath @parameters -ReuseSnapshot
    }
    Assert-Fails -MessagePattern 'not an explicit source override' -Action {
        & $scriptPath @parameters -Ref ('4' * 40)
    }

    $source | ConvertTo-Json | Set-Content -LiteralPath $snapshotPath -Encoding utf8
    $hash = (Get-FileHash -LiteralPath $snapshotPath).Hash
    $restored = & $scriptPath @parameters -ReuseSnapshot
    if ($restored.resolvedCommit -cne $source.resolvedCommit -or
        $restored.msixPackageVersion -cne $source.msixPackageVersion -or
        (Get-FileHash -LiteralPath $snapshotPath).Hash -cne $hash) {
        throw 'Reusing a snapshot changed its immutable identity or bytes.'
    }
    Assert-Fails -MessagePattern 'snapshot already exists' -Action {
        & $scriptPath @parameters
    }

    foreach ($field in @(
        'packagingCommit', 'workflowRunId', 'workflowRunNumber',
        'signingMode', 'msixPackageVersion', 'msixReleaseTag'
    )) {
        $mutated = $source | ConvertTo-Json | ConvertFrom-Json
        $mutated.$field = 'unexpected'
        $mutated | ConvertTo-Json |
            Set-Content -LiteralPath $snapshotPath -Encoding utf8
        Assert-Fails -MessagePattern "unexpected workflow identity: $field" -Action {
            & $scriptPath @parameters -ReuseSnapshot
        }
    }

    $source.signingMode = 'unsigned'
    $source.msixPackageVersion = '0.1.42.1'
    $source.msixReleaseTag = 'v0.1.42.1'
    $parameters.SigningMode = 'unsigned'
    $source | ConvertTo-Json | Set-Content -LiteralPath $snapshotPath -Encoding utf8
    $restored = & $scriptPath @parameters -ReuseSnapshot
    if ($restored.msixPackageVersion -cne '0.1.42.1') {
        throw 'An unsigned retry did not retain the original run-based MSIX version.'
    }
    Assert-Fails -MessagePattern 'requested selector' -Action {
        & $scriptPath @parameters -Ref 'main' -ReuseSnapshot
    }

    $baseUri = 'https://api.github.com/repos/openclaw/openclaw'
    $registryUri = 'https://registry.npmjs.org/openclaw'
    $responses["$registryUri/latest"] = @{
        name = 'openclaw'; version = $version
    }
    $responses["$baseUri/git/ref/tags/v$version"] = @{
        ref = "refs/tags/v$version"
        object = @{ type = 'tag'; sha = '3' * 40 }
    }
    $responses["$baseUri/git/tags/$('3' * 40)"] = @{
        sha = '3' * 40
        tag = "v$version"
        verification = @{ verified = $true; reason = 'valid' }
        object = @{ type = 'commit'; sha = '2' * 40 }
    }
    $responses["$baseUri/contents/package.json?ref=$('2' * 40)"] = @{
        type = 'file'
        encoding = 'base64'
        content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(
            (@{ name = 'openclaw'; version = $version } | ConvertTo-Json)))
    }
    $responses["$registryUri/$version"] = @{
        name = 'openclaw'; version = $version
        repository = $policy.repository
        dist = @{ integrity = $source.registryIntegrity }
    }
    $freshParameters = $parameters.Clone()
    $freshParameters.OutputPath = Join-Path $testRoot 'fresh\source-resolution.json'
    $freshParameters.SigningMode = 'official'
    $fresh = & $scriptPath @freshParameters
    if ($fresh -isnot [pscustomobject] -or
        $fresh.msixPackageVersion -cne "$version.$($policy.packageRevision)" -or
        $fresh.resolvedCommit -cne ('2' * 40) -or $requests.Count -ne 5) {
        throw 'A new workflow did not save the resolved source and derived release identity.'
    }
    $hash = (Get-FileHash -LiteralPath $freshParameters.OutputPath).Hash
    $responses.Clear()
    $replayed = & $scriptPath @freshParameters -ReuseSnapshot
    if ($requests.Count -ne 5 -or $replayed.resolvedCommit -cne $fresh.resolvedCommit -or
        (Get-FileHash -LiteralPath $freshParameters.OutputPath).Hash -cne $hash) {
        throw 'A retry queried the channel or changed the original source snapshot.'
    }

    $corrected = $fresh | ConvertTo-Json | ConvertFrom-Json
    $corrected.packageVersion = "$version-1"
    $corrected.releaseTag = "v$version-1"
    $corrected.msixPackageVersion = "$version.$(1 + $policy.packageRevision)"
    $corrected.msixReleaseTag = "v$($corrected.msixPackageVersion)"
    $corrected | ConvertTo-Json |
        Set-Content -LiteralPath $freshParameters.OutputPath -Encoding utf8
    $replayedCorrection = & $scriptPath @freshParameters -ReuseSnapshot
    if ($replayedCorrection.msixPackageVersion -cne $corrected.msixPackageVersion) {
        throw 'A stable numeric correction did not retain its numeric MSIX identity.'
    }

    $pinnedPolicy = $policy | ConvertTo-Json | ConvertFrom-Json
    $pinnedPolicy | Add-Member -NotePropertyName stableVersion -NotePropertyValue '2026.8.2'
    $pinnedPolicyPath = Join-Path $testRoot 'pinned-policy.json'
    $pinnedPolicy | ConvertTo-Json | Set-Content -LiteralPath $pinnedPolicyPath -Encoding utf8
    $pinnedParameters = $freshParameters.Clone()
    $pinnedParameters.PolicyPath = $pinnedPolicyPath
    $pinned = $fresh | ConvertTo-Json | ConvertFrom-Json
    $pinned.requestedRef = '2026.8.2'
    $pinned.packageVersion = '2026.8.2'
    $pinned.releaseTag = 'v2026.8.2'
    $pinned.msixPackageVersion = "2026.8.2.$($policy.packageRevision)"
    $pinned.msixReleaseTag = "v$($pinned.msixPackageVersion)"
    $pinned | ConvertTo-Json | Set-Content -LiteralPath $pinnedParameters.OutputPath -Encoding utf8
    $replayedPin = & $scriptPath @pinnedParameters -ReuseSnapshot
    if ($replayedPin.channel -cne 'stable' -or $replayedPin.requestedRef -cne '2026.8.2') {
        throw 'A reviewed compatibility pin did not remain on stable.'
    }
    $pinnedPolicy.stableVersion = $version
    $pinnedPolicy | ConvertTo-Json | Set-Content -LiteralPath $pinnedPolicyPath -Encoding utf8
    Assert-Fails -MessagePattern 'requestedRef|stableVersion|policy' -Action {
        & $scriptPath @pinnedParameters -ReuseSnapshot
    }

    $retired = $fresh | ConvertTo-Json | ConvertFrom-Json
    $retired.channel = 'extended-stable'
    $retired.requestedRef = 'extended-stable'
    $retired.packageVersion = '2026.6.35'
    $retired.releaseTag = 'v2026.6.35'
    $retired | ConvertTo-Json | Set-Content -LiteralPath $freshParameters.OutputPath -Encoding utf8
    Assert-Fails -MessagePattern 'channel|packageVersion|requestedRef' -Action {
        & $scriptPath @freshParameters -ReuseSnapshot
    }
    Write-Host 'Workflow source snapshot tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
