[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$testFile = Join-Path $repositoryRoot 'tests\node\native-redirect.test.mjs'
$testRoot = Join-Path $env:TEMP (
    "openclaw-native-redirect-$([guid]::NewGuid().ToString('N'))"
)

$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$failed = $true
try {
    New-Item -Path $testRoot -ItemType Directory -Force | Out-Null

    # The suite builds its fixtures under the temporary directory. Pointing it
    # at a directory this script owns keeps cleanup complete even when a Node.js
    # process is stopped before the suite's own cleanup runs.
    $env:TEMP = $testRoot
    $env:TMP = $testRoot
    & node --test $testFile
    if ($LASTEXITCODE -ne 0) {
        throw "Native dependency redirect tests failed with exit code $LASTEXITCODE."
    }

    $failed = $false
    Write-Host 'Native dependency redirect tests passed.'
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    if (Test-Path -LiteralPath $testRoot) {
        try {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
        catch {
            # A cleanup error must not replace the failure that caused it.
            if (-not $failed) {
                throw
            }
            Write-Warning "Could not remove '$testRoot': $($_.Exception.Message)"
        }
    }
}
