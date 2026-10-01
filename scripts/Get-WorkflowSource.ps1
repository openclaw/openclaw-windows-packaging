[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PolicyPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$Ref = '',
    [ValidateSet('unsigned', 'test', 'store', 'official')][string]$SigningMode = 'unsigned',
    [Parameter(Mandatory)][ValidatePattern('\A[1-9][0-9]*\z')][string]$WorkflowRunId,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$PackagingCommit,
    [switch]$ReuseSnapshot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpenClawSource.ps1')

$policy = Read-OpenClawReleasePolicy -Path $PolicyPath
$releaseMode = $SigningMode -in @('official', 'store')
if ($releaseMode -and $Ref -ne '' -and $Ref -cnotmatch '\A[0-9a-fA-F]{40}\z') {
    throw 'Release publication requires an empty Ref or a full SHA matching stable.'
}
$sourceRef = if ($releaseMode) { '' } else { $Ref }
if ($ReuseSnapshot) {
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw 'The source snapshot is unavailable. Start a new workflow run; do not re-resolve a retry.'
    }
    $source = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json -Depth 16 -NoEnumerate
}
else {
    if (Test-Path -LiteralPath $OutputPath) { throw "The source snapshot already exists: $OutputPath" }
    $source = Resolve-OpenClawSource -Policy $policy -Ref $sourceRef
}
Assert-OpenClawSource -Source $source -Policy $policy -RequireChannel:$releaseMode
$expectedRef = if ($sourceRef -eq '') { Get-OpenClawPolicyRef $policy } else { $sourceRef }
if ($source.requestedRef -cne $expectedRef -or (($sourceRef -eq '') -ne ($source.channel -ceq 'stable'))) {
    throw 'The source snapshot does not match the requested selector.'
}
if ($releaseMode -and $Ref -ne '' -and $Ref -ine $source.resolvedCommit) {
    throw 'The requested full SHA does not match the captured stable release.'
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
