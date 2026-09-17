[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PolicyPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$Ref = '',
    [ValidateSet('unsigned', 'test', 'official')][string]$SigningMode = 'unsigned',
    [Parameter(Mandatory)][ValidatePattern('\A[1-9][0-9]*\z')][string]$WorkflowRunId,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$PackagingCommit,
    [switch]$ReuseSnapshot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpenClawSource.ps1')

$policy = Read-OpenClawReleasePolicy -Path $PolicyPath
if ($SigningMode -eq 'official' -and $Ref -ne '' -and
    ($Ref -cnotmatch '\A[0-9a-fA-F]{40}\z' -or $Ref -ine $policy.approvedCommit)) {
    throw 'Official signing requires the full reviewed approvedCommit for an explicit Ref.'
}
if ($ReuseSnapshot) {
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw 'The source snapshot is unavailable. Start a new workflow run; do not re-resolve a retry.'
    }
    $source = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json -Depth 16 -NoEnumerate
}
else {
    if (Test-Path -LiteralPath $OutputPath) { throw "The source snapshot already exists: $OutputPath" }
    $source = Resolve-OpenClawSource -Policy $policy -Ref $Ref
}
Assert-OpenClawSource -Source $source -Policy $policy
$expectedRef = if ($Ref -eq '') { Get-OpenClawPolicyRef $policy } else { $Ref }
if ($source.requestedRef -cne $expectedRef -or (($Ref -eq '') -ne ($source.channel -ceq 'stable'))) {
    throw 'The source snapshot does not match the requested selector.'
}
if ($SigningMode -eq 'official') {
    Assert-OpenClawSourceText $policy.approvedCommit 'approvedCommit' -Pattern '\A[0-9a-fA-F]{40}\z'
    Assert-OpenClawSourceText $policy.payloadPackageVersion 'payloadPackageVersion'
    Assert-OpenClawSourceText $policy.gatewayTag 'gatewayTag'
    if ($source.resolvedCommit -ine $policy.approvedCommit -or
        $source.packageVersion -cne $policy.payloadPackageVersion -or
        "v$($source.packageVersion)" -cne $policy.gatewayTag) {
        throw 'Official signing requires the reviewed approvedCommit, payloadPackageVersion, and gatewayTag.'
    }
}
$context = [ordered]@{
    workflowRunId = $WorkflowRunId
    packagingCommit = $PackagingCommit.ToLowerInvariant()
    signingMode = $SigningMode
}
foreach ($field in $context.Keys) {
    if ($ReuseSnapshot) {
        $value = Get-OpenClawSourceField $source $field
        if ($value -isnot [string] -or $value -cne $context[$field]) {
            throw "The source snapshot has an unexpected workflow identity: $field"
        }
    }
    else { $source | Add-Member -NotePropertyName $field -NotePropertyValue $context[$field] }
}
if (-not $ReuseSnapshot) {
    $path = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
        ($source | ConvertTo-Json -Depth 4).Replace("`r`n", "`n") + "`n")
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
}
return $source
