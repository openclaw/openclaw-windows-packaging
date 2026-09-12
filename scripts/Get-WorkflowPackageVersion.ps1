[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [long]$RunNumber,

    [Parameter(Mandatory)]
    [long]$RunAttempt,

    [string]$ReleaseVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$maximumComponent = 65534L
$componentBase = $maximumComponent + 1L

if (-not [string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    $releaseMatch = [regex]::Match(
        $ReleaseVersion.Trim(),
        '^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)\.(?<revision>\d+)$'
    )
    if (-not $releaseMatch.Success) {
        throw (
            "ReleaseVersion '$ReleaseVersion' must contain exactly four " +
            'numeric components, such as 0.0.0.0.'
        )
    }

    $components = @(
        [long]::Parse($releaseMatch.Groups['major'].Value),
        [long]::Parse($releaseMatch.Groups['minor'].Value),
        [long]::Parse($releaseMatch.Groups['build'].Value),
        [long]::Parse($releaseMatch.Groups['revision'].Value)
    )
    foreach ($component in $components) {
        if ($component -gt $maximumComponent) {
            throw (
                "ReleaseVersion '$ReleaseVersion' contains a component greater than " +
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
