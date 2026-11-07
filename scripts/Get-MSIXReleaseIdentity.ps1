[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GatewayTag,

    [Parameter(Mandatory)]
    [int]$MSIXRevision
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$match = [regex]::Match(
    $GatewayTag.Trim(),
    '^v(?<year>\d{4})\.(?<month>\d{1,2})\.(?<patch>\d{1,5})(?:-(?:(?<correction>\d+)|(?<channel>beta)\.(?<sequence>[1-9][0-9]{0,3})))?$'
)
if (-not $match.Success) {
    throw (
        "GatewayTag '$GatewayTag' must be an OpenClaw release tag " +
        'such as v2026.9.4, v2026.7.1-2, or v2026.9.4-beta.1.'
    )
}

[int]$year = $match.Groups['year'].Value
[int]$month = $match.Groups['month'].Value
[int]$patch = $match.Groups['patch'].Value
[int]$correction = if ($match.Groups['correction'].Success) {
    [int]$parsedCorrection = $match.Groups['correction'].Value
    if ($parsedCorrection -lt 2 -or $parsedCorrection -gt 9) {
        throw 'The Gateway correction suffix must be between 2 and 9.'
    }
    $parsedCorrection
}
else {
    0
}

if ($year -lt 1 -or $year -gt 9999) {
    throw 'The Gateway release year must be between 1 and 9999.'
}
if ($month -lt 1 -or $month -gt 12) {
    throw 'The Gateway release month must be between 1 and 12.'
}
if ($patch -lt 1 -or $patch -gt 99) {
    throw 'The Gateway monthly release sequence must be between 1 and 99.'
}
if ($MSIXRevision -lt 0 -or $MSIXRevision -gt 9) {
    throw 'MSIXRevision must be between 0 and 9.'
}

# Pack the two-digit monthly release sequence, one-digit Gateway correction,
# and one-digit MSIX rebuild into the third component. Partner Center reserves
# the fourth component as zero. Decimal place value preserves release ordering.
$packageBuild = ($patch * 100) + ($correction * 10) + $MSIXRevision
$packageVersion = "$year.$month.$packageBuild.0"
$channel = 'stable'
if ($match.Groups['channel'].Success) {
    $channel = $match.Groups['channel'].Value
    $sequence = [int]$match.Groups['sequence'].Value
    if ($sequence -gt 2999) { throw 'The prerelease sequence must be between 1 and 2999.' }
    # Sideload previews occupy the gap after all rebuilds of the preceding
    # release and before this release's unchanged stable version. The nonzero
    # fourth component is deliberately not a Microsoft Store version.
    $packageBuild = ($patch * 100) - 1
    $previewRevision = ($sequence * 10) + $MSIXRevision
    $packageVersion = "$year.$month.$packageBuild.$previewRevision"
}
$releaseTag = "$($GatewayTag.Trim())-msix.$MSIXRevision"

[pscustomobject]@{
    GatewayTag = $GatewayTag.Trim()
    MSIXRevision = $MSIXRevision
    PackageVersion = $packageVersion
    ReleaseTag = $releaseTag
    ReleaseVersion = $releaseTag.Substring(1)
    Channel = $channel
    Prerelease = $channel -ne 'stable'
    MakeLatest = $channel -eq 'stable'
}
