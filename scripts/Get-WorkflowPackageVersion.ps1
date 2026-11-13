[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [long]$RunNumber,

    [Parameter(Mandatory)]
    [long]$RunAttempt,

    [string]$ReleaseVersion,

    [switch]$Store
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

    if ($Store) {
        # The Store identity has its own upgrade sequence. Reserve the range
        # above every shipped source-derived version (at most 9999), and use
        # GitHub's immutable workflow run number across stable and preview runs.
        # A retry reuses its version; never wrap or reset this sequence.
        $storeVersionOffset = 10000L
        if ($components[0] -lt 1 -or $components[1] -lt 1 -or $components[1] -gt 12) {
            throw 'Store ReleaseVersion must carry the Gateway release year and month.'
        }
        if ($RunNumber -lt 1 -or $RunNumber -gt ($maximumComponent - $storeVersionOffset)) {
            throw 'Store workflow run number exceeds the supported allocation range (1..55534).'
        }
        return "$($components[0]).$($components[1]).$($storeVersionOffset + $RunNumber).0"
    }
    return ($components -join '.')
}

if ($Store) { throw 'Store version allocation requires ReleaseVersion.' }

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
