[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PolicyPath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [ValidateSet('unsigned', 'test', 'official')]
    [string]$SigningMode = 'unsigned',

    [string]$Ref = '',

    [Parameter(Mandatory)]
    [long]$RunNumber,

    [Parameter(Mandatory)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$WorkflowRunId,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$PackagingCommit,

    [switch]$ReuseSnapshot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpenClawSource.ps1')

$policy = Read-OpenClawReleasePolicy -Path $PolicyPath
if ($SigningMode -eq 'official' -and $Ref -ne '') {
    throw 'Official signing requires the policy channel, not an explicit source override.'
}

if ($ReuseSnapshot) {
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw 'The source snapshot is unavailable. Start a new workflow run; do not re-resolve a retry.'
    }
    $source = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
}
else {
    if (Test-Path -LiteralPath $OutputPath) {
        throw "The source snapshot already exists: $OutputPath"
    }
    $source = Resolve-OpenClawSource -Policy $policy -Ref $Ref
}

Assert-OpenClawSource -Source $source -Policy $policy `
    -RequireChannel:($SigningMode -eq 'official')
$expectedRef = if ($Ref -eq '') { $policy.channel } else { $Ref }
if ($source.requestedRef -cne $expectedRef) {
    throw 'The source snapshot does not match the requested selector.'
}

$versionParameters = @{
    RunNumber = $RunNumber
    RunAttempt = 1
}
if ($SigningMode -eq 'official') {
    $versionParameters.ReleaseVersion =
        "$($source.packageVersion).$($policy.packageRevision)"
}
$packageVersion = & (Join-Path $PSScriptRoot 'Get-WorkflowPackageVersion.ps1') `
    @versionParameters
$context = [ordered]@{
    packagingCommit = $PackagingCommit.ToLowerInvariant()
    workflowRunId = $WorkflowRunId
    workflowRunNumber = $RunNumber
    signingMode = $SigningMode
    msixPackageVersion = $packageVersion
    msixReleaseTag = "v$packageVersion"
}

foreach ($field in $context.Keys) {
    if ($ReuseSnapshot) {
        if ($source.$field -cne $context[$field]) {
            throw "The source snapshot has an unexpected workflow identity: $field"
        }
    }
    else {
        $source | Add-Member -NotePropertyName $field -NotePropertyValue $context[$field]
    }
}

if (-not $ReuseSnapshot) {
    $parent = Split-Path ([IO.Path]::GetFullPath($OutputPath)) -Parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($OutputPath),
        ($source | ConvertTo-Json -Depth 4) + "`n",
        [Text.UTF8Encoding]::new($false))
}

return $source
