[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
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
$sourceSelection = $sourceMetadata |
    Select-Object channel, releaseTag, tagObject, resolvedAt, registryIntegrity
. (Join-Path $PSScriptRoot 'OpenClawSource.ps1')
Assert-OpenClawSourceVersion -Version $sourceMetadata.packageVersion -Final
if ($null -ne $sourceMetadata.PSObject.Properties['channel']) {
    $policy = Read-OpenClawReleasePolicy -Path (
        Join-Path (Split-Path $PSScriptRoot -Parent) 'release-policy.json')
    Assert-OpenClawSource -Source $sourceMetadata -Policy $policy
}
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

$stagingDirectory = Join-Path $env:RUNNER_TEMP "openclaw-stage-$Architecture"
Remove-Item $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item $stagingDirectory -ItemType Directory | Out-Null
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null

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

$installedPackage = Join-Path $stagingDirectory 'node_modules\openclaw'
foreach ($requiredPath in @('package.json', 'openclaw.mjs', 'dist')) {
    $path = Join-Path $installedPackage $requiredPath
    if (-not (Test-Path $path)) {
        throw "Staged package is missing required path: $path"
    }
}

$installedManifest = Get-Content `
    -LiteralPath (Join-Path $installedPackage 'package.json') -Raw |
    ConvertFrom-Json
if ($installedManifest.name -cne 'openclaw' -or
    $installedManifest.version -cne $sourceMetadata.packageVersion) {
    throw 'The installed OpenClaw package does not match the source identity.'
}

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
    channel          = $sourceSelection.channel
    releaseTag       = $sourceSelection.releaseTag
    tagObject        = $sourceSelection.tagObject
    resolvedAt       = $sourceSelection.resolvedAt
    registryIntegrity = $sourceSelection.registryIntegrity
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
