[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OpenClawDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$applicationDirectory = (Resolve-Path -LiteralPath $OpenClawDirectory).Path
if ((Split-Path -Leaf $applicationDirectory) -ne 'app') {
    throw 'OpenClawDirectory must name the app directory inside a prepared payload.'
}
$payloadDirectory = Split-Path -Parent $applicationDirectory
& node (Join-Path $PSScriptRoot 'fixtures\gateway-isolation-context.mjs') $payloadDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Gateway isolation context proof failed with exit code $LASTEXITCODE."
}
