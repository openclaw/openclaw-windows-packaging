[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ValidatedScriptSource {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors
    )
    if ($errors.Count -ne 0) {
        throw "PowerShell parser errors in $Path`: $($errors -join '; ')"
    }
    return Get-Content -LiteralPath $Path -Raw
}

$upgradeSource = Get-ValidatedScriptSource (
    Join-Path $PSScriptRoot 'Test-MSIXUpgrade.ps1'
)
$storeSource = Get-ValidatedScriptSource (
    Join-Path $PSScriptRoot 'Test-MSIXStoreUpgrade.ps1'
)

if ($upgradeSource -notmatch '(?s)if \(\s*\$TransitionMode -ceq ''in-place''.*?-not \$markerContentsMatch') {
    throw 'In-place upgrade validation must require the exact baseline marker contents.'
}
if ($upgradeSource -notmatch '(?s)elseif \(\s*\$installedCandidate\.PackageFamilyName.*?or\s*\$markerExists\s*\)') {
    throw 'Identity-reset validation must reject any candidate LocalState marker.'
}

$installMatch = [regex]::Match(
    $storeSource,
    '(?ms)^function Install-StoreBaseline \{(?<body>.*?)(?=^function |^\$certificate = )'
)
if (-not $installMatch.Success) {
    throw 'Unable to locate Install-StoreBaseline.'
}
$installBody = $installMatch.Groups['body'].Value
$ownershipIndex = $installBody.IndexOf(
    '$script:testOwnsPackage = $true',
    [StringComparison]::Ordinal
)
$wingetIndex = $installBody.IndexOf(
    '& $winget.Source install',
    [StringComparison]::Ordinal
)
if ($ownershipIndex -lt 0 -or $wingetIndex -lt 0 -or $ownershipIndex -gt $wingetIndex) {
    throw 'Store package cleanup must be armed before WinGet begins installation.'
}

if ($storeSource -notmatch '(?s)finally \{\s*try \{\s*Remove-TestPackage\s*\}\s*finally \{.*?TrustedPeople') {
    throw 'Certificate cleanup must run in a nested finally after package cleanup.'
}

Write-Host 'MSIX upgrade safety regression tests passed.'
