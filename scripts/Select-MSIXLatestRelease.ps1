[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateTag,
    [Parameter(Mandatory)][string]$LatestTag
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-PublishedIdentity([string]$Tag) {
    if ($Tag -cnotmatch '\A(?<gateway>v.+)-msix\.(?<revision>[0-9])\z') {
        throw "Unrecognized Gateway MSIX release tag '$Tag'; refusing to change Latest."
    }
    & (Join-Path $PSScriptRoot 'Get-MSIXReleaseIdentity.ps1') `
        -GatewayTag $Matches.gateway -MSIXRevision ([int]$Matches.revision)
}
$candidate = Read-PublishedIdentity $CandidateTag
$latest = Read-PublishedIdentity $LatestTag
if ($latest.Prerelease) { throw 'GitHub Latest must identify a stable Gateway release.' }
# Compare the installed version owner, not SemVer: stable correction suffixes
# and MSIX rebuilds must retain the same ordering as Windows upgrades.
return (-not $candidate.Prerelease -and [version]$candidate.PackageVersion -ge [version]$latest.PackageVersion)
