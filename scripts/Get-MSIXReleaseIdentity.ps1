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
    '^v(?<year>\d{4})\.(?<month>\d{1,2})\.(?<patch>\d{1,2})(?:-(?<correction>\d+))?$'
)
if (-not $match.Success) {
    throw (
        "GatewayTag '$GatewayTag' must be a stable OpenClaw release tag " +
        'such as v2026.9.4 or v2026.7.1-2.'
    )
}

[int]$year = $match.Groups['year'].Value
[int]$month = $match.Groups['month'].Value
[int]$patch = $match.Groups['patch'].Value
[int]$correction = if ($match.Groups['correction'].Success) {
    $match.Groups['correction'].Value
}
else {
    0
}

if ($year -lt 1 -or $year -gt 65534) {
    throw 'The Gateway release year must be between 1 and 65534.'
}
if ($month -lt 1 -or $month -gt 12) {
    throw 'The Gateway release month must be between 1 and 12.'
}
if ($patch -lt 0 -or $patch -gt 64) {
    throw 'The Gateway patch must be between 0 and 64.'
}
if ($correction -lt 0 -or $correction -gt 9) {
    throw 'The Gateway correction must be between 0 and 9.'
}
if ($MSIXRevision -lt 0 -or $MSIXRevision -gt 99) {
    throw 'MSIXRevision must be between 0 and 99.'
}

# Keep the fourth component at zero for Microsoft Store compatibility. Pack
# the Gateway patch, optional correction, and independent packaging revision
# into the build component while preserving their upgrade ordering.
[int]$build = ($patch * 1000) + ($correction * 100) + $MSIXRevision
$packageVersion = "$year.$month.$build.0"
$releaseTag = "$($GatewayTag.Trim())-msix.$MSIXRevision"

[pscustomobject]@{
    GatewayTag = $GatewayTag.Trim()
    MSIXRevision = $MSIXRevision
    PackageVersion = $packageVersion
    ReleaseTag = $releaseTag
    ReleaseVersion = $releaseTag.Substring(1)
}
