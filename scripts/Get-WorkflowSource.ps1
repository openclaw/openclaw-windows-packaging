[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PolicyPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$Ref = '',
    [string]$GatewayVersion = '',
    [ValidateSet('unsigned', 'test', 'store', 'official')][string]$SigningMode = 'unsigned',
    [Parameter(Mandatory)][ValidatePattern('\A[1-9][0-9]*\z')][string]$WorkflowRunId,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$PackagingCommit,
    [switch]$ReuseSnapshot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpenClawSource.ps1')

$policy = Read-OpenClawReleasePolicy -Path $PolicyPath
$selectionPolicy = $policy | ConvertTo-Json -Depth 16 | ConvertFrom-Json -Depth 16 -NoEnumerate
$null = Assert-OpenClawSourceText $GatewayVersion 'GatewayVersion' -AllowEmpty
if ($GatewayVersion -ne '') {
    Assert-OpenClawSourceVersion $GatewayVersion
    # The payload selector is an effective one-run policy pin. Release identity
    # continues from the unmodified stable selector and is captured beside it.
    $selectionPolicy |
        Add-Member -NotePropertyName stableVersion -NotePropertyValue $GatewayVersion -Force
}
$releaseMode = $SigningMode -in @('official', 'store')
$refAssertion = $releaseMode -and $GatewayVersion -ne '' -and $Ref -ne ''
if ($Ref -ne '' -and $GatewayVersion -ne '') {
    if (-not $refAssertion) {
        throw 'Ref and GatewayVersion cannot both select the payload source.'
    }
    if ($Ref -cnotmatch '\A[0-9a-fA-F]{40}\z') {
        throw 'A release GatewayVersion accepts only a full SHA Ref assertion.'
    }
}
$sourceRef = if ($refAssertion) { '' } else { $Ref }
$separateReleaseIdentity = $GatewayVersion -ne '' -or ($releaseMode -and $sourceRef -ne '')
if ($ReuseSnapshot) {
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw 'The source snapshot is unavailable. Start a new workflow run; do not re-resolve a retry.'
    }
    $source = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json -Depth 16 -NoEnumerate
}
else {
    if (Test-Path -LiteralPath $OutputPath) { throw "The source snapshot already exists: $OutputPath" }
    $source = Resolve-OpenClawSource -Policy $selectionPolicy -Ref $sourceRef
    if ($separateReleaseIdentity) {
        $releaseIdentitySource = Resolve-OpenClawSource -Policy $policy
        $source | Add-Member releaseIdentitySource $releaseIdentitySource
    }
}
Assert-OpenClawSource `
    -Source $source `
    -Policy $selectionPolicy `
    -RequireChannel:($releaseMode -and $sourceRef -eq '')
$expectedRef = if ($sourceRef -eq '') { Get-OpenClawPolicyRef $selectionPolicy } else { $sourceRef }
if ($source.requestedRef -cne $expectedRef -or (($sourceRef -eq '') -ne ($source.channel -ceq 'stable'))) {
    throw 'The source snapshot does not match the requested selector.'
}
$releaseIdentityProperty = $source.PSObject.Properties['releaseIdentitySource']
if ($separateReleaseIdentity) {
    if ($null -eq $releaseIdentityProperty) {
        throw 'The source snapshot is missing its release identity source.'
    }
    $releaseIdentitySource = $releaseIdentityProperty.Value
    Assert-OpenClawSource -Source $releaseIdentitySource -Policy $policy -RequireChannel
    $payloadTag = if ($source.releaseTag -ne '') {
        $source.releaseTag
    }
    else { "v$($source.packageVersion)" }
    $payloadIdentity = & (Join-Path $PSScriptRoot 'Get-MSIXReleaseIdentity.ps1') `
        -GatewayTag $payloadTag -MSIXRevision 0
    $releaseIdentity = & (Join-Path $PSScriptRoot 'Get-MSIXReleaseIdentity.ps1') `
        -GatewayTag $releaseIdentitySource.releaseTag -MSIXRevision 0
    if ($GatewayVersion -ne '' -and
        [version]$releaseIdentity.PackageVersion -le [version]$payloadIdentity.PackageVersion) {
        throw 'GatewayVersion must select a release older than the current stable release identity.'
    }
    if ($sourceRef -ne '' -and
        [version]$releaseIdentity.PackageVersion -lt [version]$payloadIdentity.PackageVersion) {
        throw 'Ref must select a payload version that is not newer than the current stable release identity.'
    }
}
elseif ($null -ne $releaseIdentityProperty) {
    throw 'The source snapshot has an unexpected release identity source.'
}
if ($refAssertion -and $Ref -ine $source.resolvedCommit) {
    throw 'The requested full SHA does not match the captured GatewayVersion release.'
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
