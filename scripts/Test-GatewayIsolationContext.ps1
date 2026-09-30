[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OpenClawDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedApplication = Resolve-Path -LiteralPath $OpenClawDirectory
if ($resolvedApplication.Provider.Name -ne 'FileSystem' -or
    -not (Test-Path -LiteralPath $resolvedApplication.ProviderPath -PathType Container)) {
    throw 'Prepared OpenClaw application must be a filesystem directory.'
}
$applicationDirectory = $resolvedApplication.ProviderPath
Push-Location $applicationDirectory
try {
    & node (Join-Path $PSScriptRoot 'fixtures\gateway-isolation-context.mjs')
}
finally {
    Pop-Location
}
if ($LASTEXITCODE -ne 0) {
    throw "Gateway isolation context proof failed with exit code $LASTEXITCODE."
}
