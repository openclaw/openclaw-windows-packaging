[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$scriptPath = Join-Path $PSScriptRoot 'Get-MxcRuntime.ps1'
$testRoot = Join-Path $env:TEMP "openclaw-mxc-runtime-$([guid]::NewGuid().ToString('N'))"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

try {
    $source = Join-Path $testRoot 'source'
    $package = Join-Path $source 'package'
    $output = Join-Path $testRoot 'staged'
    $scripts = Join-Path $testRoot 'scripts'
    $archive = Join-Path $testRoot 'mxc-fixture.tgz'
    New-Item -Path $package, $output, $scripts -ItemType Directory -Force | Out-Null
    $runtimeFile = Join-Path $package 'fixture.bin'
    [IO.File]::WriteAllText($runtimeFile, 'verified runtime')
    & tar -czf $archive -C $source 'package/fixture.bin'
    if ($LASTEXITCODE -ne 0) {
        throw "Creating the fixture archive failed (exit $LASTEXITCODE)."
    }

    $stream = [IO.File]::OpenRead($archive)
    try {
        $sha512 = [Security.Cryptography.SHA512]::Create()
        try {
            $integrity = 'sha512-' + [Convert]::ToBase64String(
                $sha512.ComputeHash($stream)
            )
        }
        finally {
            $sha512.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $hash = (Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $lock = [ordered]@{
        package = '@microsoft/mxc-sdk'
        version = 'fixture'
        wireSchemaVersion = 'fixture'
        minimumWindowsBuild = '0'
        tarballUrl = 'https://example.invalid/mxc-fixture.tgz'
        tarballIntegrity = $integrity
        architectures = @{
            x64 = @{
                files = @(@{
                    archivePath = 'package/fixture.bin'
                    stagedPath = 'fixture.bin'
                    length = (Get-Item -LiteralPath $runtimeFile).Length
                    sha256 = $hash
                })
            }
        }
        licenseFiles = @()
    } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $testRoot 'mxc-runtime.lock.json'), $lock)

    $fixtureScript = Join-Path $scripts 'Get-MxcRuntime.ps1'
    Copy-Item -LiteralPath $scriptPath -Destination $fixtureScript
    & $fixtureScript -Architecture x64 -ArchivePath $archive -OutputDirectory $output
    if ($LASTEXITCODE -ne 0) {
        throw "Initial MXC staging failed (exit $LASTEXITCODE)."
    }

    $unexpected = Join-Path $output 'unverified.bin'
    [IO.File]::WriteAllText($unexpected, 'must not be packaged')
    & $fixtureScript -Architecture x64 -ArchivePath $archive -OutputDirectory $output
    if ($LASTEXITCODE -ne 0) {
        throw "MXC restaging failed (exit $LASTEXITCODE)."
    }

    Assert-True (-not (Test-Path -LiteralPath $unexpected)) (
        'MXC runtime reuse retained an unpinned file.'
    )
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'fixture.bin')) (
        'MXC restaging did not preserve the pinned runtime file.'
    )
    Write-Host 'MXC runtime staging tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
