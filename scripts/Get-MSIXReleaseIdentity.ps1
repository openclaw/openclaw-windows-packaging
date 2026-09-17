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
    '^v(?<year>\d{4})\.(?<month>\d{1,2})\.(?<patch>\d{1,5})(?:-(?<correction>\d+))?$'
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
[int]$releaseSequence = if ($match.Groups['correction'].Success) {
    [int]$correction = $match.Groups['correction'].Value
    if ($correction -lt 2 -or $correction -gt 64) {
        throw 'The Gateway correction suffix must be between 2 and 64.'
    }
    $correction
}
else {
    1
}

if ($year -lt 1 -or $year -gt 9999) {
    throw 'The Gateway release year must be between 1 and 9999.'
}
if ($month -lt 1 -or $month -gt 12) {
    throw 'The Gateway release month must be between 1 and 12.'
}
if ($patch -lt 0 -or $patch -gt 65534) {
    throw 'The Gateway patch must be between 0 and 65534.'
}
if ($MSIXRevision -lt 0 -or $MSIXRevision -gt 999) {
    throw 'MSIXRevision must be between 0 and 999.'
}

# Give every Gateway release sequence 1,000 deterministic MSIX revision slots. The
# unsuffixed tag is sequence 1 and correction tags use their numeric suffix. This
# prevents an MSIX-only rebuild from consuming the number assigned to a later
# Gateway correction.
$packageRevision = ($releaseSequence * 1000) + $MSIXRevision
if ($packageRevision -gt 65534) {
    throw 'The combined Gateway release sequence and MSIXRevision must not exceed 65534.'
}
$packageVersion = "$year.$month.$patch.$packageRevision"
$releaseTag = "$($GatewayTag.Trim())-msix.$MSIXRevision"

[pscustomobject]@{
    GatewayTag = $GatewayTag.Trim()
    MSIXRevision = $MSIXRevision
    PackageVersion = $packageVersion
    ReleaseTag = $releaseTag
    ReleaseVersion = $releaseTag.Substring(1)
}
