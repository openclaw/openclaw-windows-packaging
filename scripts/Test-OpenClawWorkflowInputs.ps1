[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('unsigned', 'test', 'official')]
    [string]$SigningMode,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RequestedRef,

    [Parameter(Mandatory)]
    [string]$EventName,

    [Parameter(Mandatory)]
    [string]$GitRef,

    [string]$PolicyPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'release-policy.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$approvedCommit = [string]$policy.approvedCommit
if ($approvedCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'The official OpenClaw commit approval is invalid.'
}
if ($SigningMode -eq 'official') {
    if ($EventName -ne 'workflow_dispatch' -or $GitRef -ne 'refs/heads/main') {
        throw 'Official signing requires a manual workflow_dispatch run from main.'
    }
    if ($RequestedRef -ine $approvedCommit) {
        throw (
            'Official signing cannot use an unapproved or development ref. ' +
            "Set openclaw_ref to $approvedCommit, or select signing_mode " +
            "'unsigned' or 'test' to qualify the unreleased development runtime."
        )
    }
}
Write-Host "Validated OpenClaw workflow inputs for $SigningMode signing."
