[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [long]$RunNumber,

    [Parameter(Mandatory)]
    [long]$RunAttempt,

    [string]$ReleaseTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$maximumComponent = 65534L
$componentBase = $maximumComponent + 1L

if (-not [string]::IsNullOrWhiteSpace($ReleaseTag)) {
    $releaseMatch = [regex]::Match(
        $ReleaseTag.Trim(),
        '^v(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)(?:-(?<revision>[1-9]\d*))?$'
    )
    if (-not $releaseMatch.Success) {
        throw (
            "ReleaseTag '$ReleaseTag' must be a stable Gateway tag such as " +
            'v2026.9.4 or a correction tag such as v2026.7.1-2.'
        )
    }

    $components = @(
        [long]::Parse($releaseMatch.Groups['major'].Value),
        [long]::Parse($releaseMatch.Groups['minor'].Value),
        [long]::Parse($releaseMatch.Groups['build'].Value),
        $(if ($releaseMatch.Groups['revision'].Success) {
            [long]::Parse($releaseMatch.Groups['revision'].Value)
        }
        else {
            0L
        })
    )
    foreach ($component in $components) {
        if ($component -gt $maximumComponent) {
            throw (
                "ReleaseTag '$ReleaseTag' contains a component greater than " +
                "$maximumComponent."
            )
        }
    }

    return ($components -join '.')
}

if ($RunNumber -lt 1) {
    throw 'RunNumber must be greater than zero.'
}
if ($RunAttempt -lt 1 -or $RunAttempt -gt $maximumComponent) {
    throw "RunAttempt must be between 1 and $maximumComponent."
}

[long]$buildComponent = 0
[long]$runNumberCarry = [Math]::DivRem(
    $RunNumber,
    $componentBase,
    [ref]$buildComponent)
[long]$minorComponent = 1L + $runNumberCarry

if ($minorComponent -gt $maximumComponent) {
    throw (
        "RunNumber $RunNumber exceeds the supported package-version range."
    )
}

"0.$minorComponent.$buildComponent.$RunAttempt"
