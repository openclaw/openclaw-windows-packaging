[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Build-MSIXBundle.ps1'
$testRoot = Join-Path $env:TEMP `
    "openclaw-bundle-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -Path $testRoot -ItemType Directory -Force | Out-Null

function Assert-Fails {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw (
                "Expected failure matching '$MessagePattern'; received: " +
                $_.Exception.Message
            )
        }
        return
    }

    throw "Expected failure matching '$MessagePattern', but the action succeeded."
}

try {
    $x64Package = Join-Path $testRoot 'x64.msix'
    $arm64Package = Join-Path $testRoot 'arm64.msix'
    Set-Content -LiteralPath $x64Package -Value 'x64' -Encoding ascii
    Set-Content -LiteralPath $arm64Package -Value 'arm64' -Encoding ascii

    $fakeMakeAppx = Join-Path $testRoot 'MakeAppx.cmd'
    $makeAppxArguments = Join-Path $testRoot 'makeappx-arguments.txt'
    $env:OPENCLAW_BUNDLE_TEST_ARGUMENTS = $makeAppxArguments
    @'
@echo off
echo %* > "%OPENCLAW_BUNDLE_TEST_ARGUMENTS%"
set output=
:parse
if "%~1"=="" goto done
if /I "%~1"=="/p" (
  set output=%~2
  shift
)
shift
goto parse
:done
if "%output%"=="" exit /b 2
type nul > "%output%"
'@ | Set-Content -LiteralPath $fakeMakeAppx -Encoding ascii

    $bundle = Join-Path $testRoot 'OpenClawGateway.msixbundle'
    & $scriptPath `
        -X64Package $x64Package `
        -Arm64Package $arm64Package `
        -PackageVersion '2026.9.4.0' `
        -OutputPath $bundle `
        -MakeAppxPath $fakeMakeAppx
    if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
        throw 'The bundle builder did not preserve the MakeAppx output.'
    }
    $arguments = Get-Content -LiteralPath $makeAppxArguments -Raw
    if ($arguments -notmatch '/bv 2026\.9\.4\.0') {
        throw "MakeAppx did not receive the expected bundle version: $arguments"
    }

    $zeroBundle = Join-Path $testRoot 'OpenClawGateway-zero.msixbundle'
    & $scriptPath `
        -X64Package $x64Package `
        -Arm64Package $arm64Package `
        -PackageVersion '0.0.0.0' `
        -OutputPath $zeroBundle `
        -MakeAppxPath $fakeMakeAppx
    $arguments = Get-Content -LiteralPath $makeAppxArguments -Raw
    if ($arguments -match '/bv') {
        throw "MakeAppx received an invalid all-zero bundle version: $arguments"
    }
    if (-not (Test-Path -LiteralPath $zeroBundle -PathType Leaf)) {
        throw 'The zero-version bundle builder did not preserve MakeAppx output.'
    }

    Assert-Fails -MessagePattern 'must be different' -Action {
        & $scriptPath `
            -X64Package $x64Package `
            -Arm64Package $x64Package `
            -PackageVersion '2026.9.4.0' `
            -OutputPath (Join-Path $testRoot 'duplicate.msixbundle') `
            -MakeAppxPath $fakeMakeAppx
    }
    Assert-Fails -MessagePattern 'four numeric components' -Action {
        & $scriptPath `
            -X64Package $x64Package `
            -Arm64Package $arm64Package `
            -PackageVersion '2026.9.4' `
            -OutputPath (Join-Path $testRoot 'bad-version.msixbundle') `
            -MakeAppxPath $fakeMakeAppx
    }
    Assert-Fails -MessagePattern 'already exists' -Action {
        & $scriptPath `
            -X64Package $x64Package `
            -Arm64Package $arm64Package `
            -PackageVersion '2026.9.4.0' `
            -OutputPath $bundle `
            -MakeAppxPath $fakeMakeAppx
    }

    Write-Host 'MSIX bundle build tests passed.'
}
finally {
    Remove-Item Env:OPENCLAW_BUNDLE_TEST_ARGUMENTS -ErrorAction SilentlyContinue
    if ([IO.Directory]::Exists($testRoot)) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
