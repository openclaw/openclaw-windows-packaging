[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [string]$ExpectedCommit,

    [Parameter(Mandatory)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory)]
    [string]$RequestedRef
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packagePath = Join-Path $PackageDirectory 'openclaw.tgz'
$metadataPath = Join-Path $PackageDirectory 'source.json'
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
    throw 'The OpenClaw package cache is incomplete.'
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw |
    ConvertFrom-Json
$normalizedCommit = $ExpectedCommit.Trim().ToLowerInvariant()
if ($normalizedCommit -notmatch '^[0-9a-f]{40}$') {
    throw "ExpectedCommit '$ExpectedCommit' is not a full commit SHA."
}
if ([string]$metadata.resolvedCommit -cne $normalizedCommit) {
    throw (
        "Cached package commit '$($metadata.resolvedCommit)' does not " +
        "match '$normalizedCommit'."
    )
}
if ([string]$metadata.packageVersion -cne $ExpectedVersion) {
    throw 'The OpenClaw package version does not match the resolved stable source.'
}

$actualHash = (
    Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
).Hash.ToLowerInvariant()
if ([string]$metadata.packageSha256 -cne $actualHash) {
    throw 'The cached OpenClaw package failed SHA-256 verification.'
}

$metadata.requestedRef = $RequestedRef
$metadata |
    ConvertTo-Json |
    Set-Content -LiteralPath $metadataPath -Encoding utf8
