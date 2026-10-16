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
    $archive = Join-Path $testRoot 'mxc-fixture.nupkg'
    New-Item -Path $package, $output, $scripts -ItemType Directory -Force | Out-Null
    $runtimeFile = Join-Path $package 'fixture.bin'
    [IO.File]::WriteAllText($runtimeFile, 'verified runtime')
    [IO.Compression.ZipFile]::CreateFromDirectory($source, $archive)

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
        package = 'Microsoft.Mxc.Sdk'
        version = 'fixture'
        minimumWindowsBuild = '0'
        archiveUrl = 'https://example.invalid/mxc-fixture.nupkg'
        archiveIntegrity = $integrity
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

    $unexpected = Join-Path $output 'unverified.bin'
    [IO.File]::WriteAllText($unexpected, 'must not be packaged')
    & $fixtureScript -Architecture x64 -ArchivePath $archive -OutputDirectory $output

    Assert-True (-not (Test-Path -LiteralPath $unexpected)) (
        'MXC runtime reuse retained an unpinned file.'
    )
    Assert-True (Test-Path -LiteralPath (Join-Path $output 'fixture.bin')) (
        'MXC restaging did not preserve the pinned runtime file.'
    )

    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $duplicate = $zip.CreateEntry('package/fixture.bin')
        $stream = $duplicate.Open()
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes('verified runtime')
            $stream.Write($bytes)
        }
        finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }
    $lockObject = $lock | ConvertFrom-Json
    $stream = [IO.File]::OpenRead($archive)
    try {
        $lockObject.archiveIntegrity = 'sha512-' + [Convert]::ToBase64String(
            [Security.Cryptography.SHA512]::HashData($stream))
    }
    finally { $stream.Dispose() }
    [IO.File]::WriteAllText((Join-Path $testRoot 'mxc-runtime.lock.json'),
        ($lockObject | ConvertTo-Json -Depth 8))
    $rejected = $false
    try {
        & $fixtureScript -Architecture x64 -ArchivePath $archive -OutputDirectory $output -Force
    }
    catch {
        $rejected = $_.Exception.Message -match 'exactly one matching'
    }
    Assert-True $rejected 'Duplicate NuGet archive entries were not rejected at extraction.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $output 'fixture.bin')) -ceq 'verified runtime') (
        'Failed archive validation modified the previously qualified runtime.'
    )
    Write-Host 'MXC runtime staging tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
