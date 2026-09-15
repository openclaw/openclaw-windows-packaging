[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [switch]$ReuseStagedInstall
)

$ErrorActionPreference = 'Stop'

$package = @(Get-ChildItem -Path $PackageDirectory -Filter '*.tgz' -File)
if ($package.Count -ne 1) {
    throw "Expected exactly one .tgz package in '$PackageDirectory'; found $($package.Count)."
}

$sourceMetadataPath = Join-Path $PackageDirectory 'source.json'
if (-not (Test-Path $sourceMetadataPath -PathType Leaf)) {
    throw "Missing source metadata: $sourceMetadataPath"
}

$sourceMetadata = Get-Content $sourceMetadataPath -Raw | ConvertFrom-Json
$nodeVersion = & node -p 'process.versions.node'
if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Unable to determine the payload build Node.js version.'
}
if ($sourceMetadata.nodeVersion -cne $nodeVersion) {
    throw (
        "Payload Node.js $nodeVersion does not match the source build " +
        "version '$($sourceMetadata.nodeVersion)'."
    )
}
$npmVersion = & npm --version
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to determine the payload build npm version.'
}

$packageHash = (
    Get-FileHash -LiteralPath $package[0].FullName -Algorithm SHA256
).Hash.ToLowerInvariant()
$stagingDirectory = Join-Path $env:RUNNER_TEMP "openclaw-stage-$Architecture"
$stagingMetadataPath = Join-Path $stagingDirectory '.openclaw-install.json'
$expectedStagingMetadata = [ordered]@{
    architecture = $Architecture
    resolvedCommit = [string]$sourceMetadata.resolvedCommit
    packageVersion = [string]$sourceMetadata.packageVersion
    nodeVersion = $nodeVersion
    npmVersion = $npmVersion
    packageSha256 = $packageHash
}
if ($ReuseStagedInstall) {
    if (-not (Test-Path -LiteralPath $stagingDirectory -PathType Container)) {
        throw "The staged OpenClaw install does not exist: $stagingDirectory"
    }
    if (-not (Test-Path -LiteralPath $stagingMetadataPath -PathType Leaf)) {
        throw "The staged OpenClaw install is missing provenance: $stagingMetadataPath"
    }
    try {
        $stagingMetadata = Get-Content -LiteralPath $stagingMetadataPath -Raw |
            ConvertFrom-Json
    }
    catch {
        throw "The staged OpenClaw install provenance is invalid: $($_.Exception.Message)"
    }
    foreach ($property in $expectedStagingMetadata.Keys) {
        if ($stagingMetadata.PSObject.Properties.Name -notcontains $property -or
            [string]$stagingMetadata.$property -cne
                [string]$expectedStagingMetadata[$property]) {
            throw (
                "The staged OpenClaw install provenance does not match " +
                "the requested $property."
            )
        }
    }
}
else {
    Remove-Item $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $stagingDirectory -ItemType Directory | Out-Null

    $previousArch = $env:npm_config_arch
    $previousTargetArch = $env:npm_config_target_arch
    try {
        $env:npm_config_arch = $Architecture
        $env:npm_config_target_arch = $Architecture

        & npm install `
            --install-strategy=nested `
            --omit=dev `
            --no-audit `
            --no-fund `
            --os=win32 `
            --cpu=$Architecture `
            --prefix $stagingDirectory `
            $package[0].FullName

        if ($LASTEXITCODE -ne 0) {
            throw "npm install failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        $env:npm_config_arch = $previousArch
        $env:npm_config_target_arch = $previousTargetArch
    }
    $expectedStagingMetadata |
        ConvertTo-Json |
        Set-Content -LiteralPath $stagingMetadataPath -Encoding utf8
}
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null

$installedPackage = Join-Path $stagingDirectory 'node_modules\openclaw'
foreach ($requiredPath in @('package.json', 'openclaw.mjs', 'dist')) {
    $path = Join-Path $installedPackage $requiredPath
    if (-not (Test-Path $path)) {
        throw "Staged package is missing required path: $path"
    }
}

& (Join-Path $PSScriptRoot 'Test-OpenClawBuildIdentity.ps1') `
    -OpenClawDirectory $installedPackage

$bundledNodeFiles = @(
    Get-ChildItem -LiteralPath $installedPackage -File -Recurse |
        Where-Object {
            $_.Name -ieq 'node.exe' -or
            $_.Name -match '^node-v\d'
        }
)
if ($bundledNodeFiles.Count -ne 0) {
    throw (
        'The OpenClaw payload must not bundle Node.js: ' +
        (
            $bundledNodeFiles |
                ForEach-Object {
                    [IO.Path]::GetRelativePath(
                        $installedPackage,
                        $_.FullName)
                } |
                Sort-Object
        ) -join ', '
    )
}

if ($Architecture -eq 'x64') {
    Push-Location $installedPackage
    try {
        & node .\openclaw.mjs --version
        if ($LASTEXITCODE -ne 0) {
            throw "OpenClaw payload smoke test failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

$applicationDirectory = Join-Path $OutputDirectory 'app'
if (Test-Path -LiteralPath $applicationDirectory) {
    Remove-Item -LiteralPath $applicationDirectory -Recurse -Force
}
Copy-Item `
    -LiteralPath $installedPackage `
    -Destination $applicationDirectory `
    -Recurse

[ordered]@{
    repository       = $sourceMetadata.repository
    requestedRef     = $sourceMetadata.requestedRef
    resolvedCommit   = $sourceMetadata.resolvedCommit
    packageVersion   = $sourceMetadata.packageVersion
    architecture     = $Architecture
    layout           = 'expanded-directory'
    nodeVersion      = $nodeVersion
    npmVersion       = $npmVersion
} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'payload-metadata.json') -Encoding utf8

$files = @(Get-ChildItem -LiteralPath $applicationDirectory -File -Recurse)
$size = ($files | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host (
    "Created expanded payload at {0} ({1} files, {2:N1} MiB)" -f
        $applicationDirectory,
        $files.Count,
        $size
)
