[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('package', 'payload')]
    [string]$Layer,

    [Parameter(Mandatory)]
    [string]$Commit,

    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$NodeVersion,

    [string]$PayloadScriptPath = (
        Join-Path $PSScriptRoot 'Build-Payload.ps1'
    ),

    [ValidateRange(1, 999)]
    [int]$FormatVersion = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$normalizedCommit = $Commit.Trim().ToLowerInvariant()
if ($normalizedCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Commit '$Commit' must be a full 40-character hexadecimal SHA."
}

if ($Layer -eq 'package') {
    return "openclaw-package-v$FormatVersion-$normalizedCommit"
}

if ([string]::IsNullOrWhiteSpace($Architecture)) {
    throw 'Architecture is required for a payload cache key.'
}
if ($NodeVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "NodeVersion '$NodeVersion' must contain three numeric components."
}
if (-not (Test-Path -LiteralPath $PayloadScriptPath -PathType Leaf)) {
    throw "Payload script does not exist: $PayloadScriptPath"
}

$scriptHash = (
    Get-FileHash -LiteralPath $PayloadScriptPath -Algorithm SHA256
).Hash.ToLowerInvariant()

@(
    "openclaw-payload-v$FormatVersion"
    $Architecture
    $normalizedCommit
    "node-$NodeVersion"
    $scriptHash
) -join '-'
