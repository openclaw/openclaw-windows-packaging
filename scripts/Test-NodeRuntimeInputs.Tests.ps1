[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw-node-inputs-$([guid]::NewGuid().ToString('N'))"
)
$previousRunnerTemp = $env:RUNNER_TEMP
$previousNpmCache = $env:npm_config_cache

function Assert-Fails {
    param([scriptblock]$Action, [string]$MessagePattern)

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw
        }
        return
    }
    throw "Expected failure matching '$MessagePattern'."
}

try {
    $env:RUNNER_TEMP = $testRoot
    $env:npm_config_cache = Join-Path $testRoot 'npm-cache'
    $source = Join-Path $testRoot 'source'
    $package = Join-Path $testRoot 'package'
    $payload = Join-Path $testRoot 'payload'
    New-Item -ItemType Directory -Path "$source\dist", $package -Force | Out-Null
    '{"name":"openclaw","version":"2026.9.4","type":"module"}' |
        Set-Content -LiteralPath "$source\package.json"
    'console.log("fixture");' | Set-Content -LiteralPath "$source\openclaw.mjs"
    'export {};' | Set-Content -LiteralPath "$source\dist\index.js"
    & npm pack $source --ignore-scripts --offline --silent --pack-destination $package
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to pack the local Node.js input fixture.'
    }
    $nodeVersion = & node -p 'process.versions.node'
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to determine the fixture Node.js version.'
    }
    $sourceMetadata = @{
        repository = 'https://github.com/openclaw/openclaw'
        requestedRef = '1' * 40
        resolvedCommit = '1' * 40
        packageVersion = '2026.9.4'
        channel = ''
        releaseTag = ''
        tagObject = ''
        resolvedAt = '2026-09-15T00:00:00.0000000Z'
        registryIntegrity = ''
        nodeVersion = $nodeVersion
    }
    $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"

    & "$PSScriptRoot\Build-Payload.ps1" `
        -PackageDirectory $package -Architecture x64 -OutputDirectory $payload
    $metadataPath = Join-Path $payload 'payload-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.nodeVersion -cne $nodeVersion) {
        throw 'The payload did not preserve the exact source build Node.js version.'
    }
    foreach ($field in @(
        'requestedRef', 'resolvedCommit', 'packageVersion', 'channel',
        'releaseTag', 'tagObject', 'registryIntegrity'
    )) {
        if ($metadata.$field -cne $sourceMetadata[$field]) {
            throw "The payload did not preserve the source selection field: $field"
        }
    }

    $sourceMetadata.packageVersion = '2026.9.3'
    $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"
    Assert-Fails -MessagePattern 'does not match the source identity' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package -Architecture x64 -OutputDirectory $payload
    }
    $sourceMetadata.packageVersion = '2026.9.4'

    foreach ($rejectedVersion in @('2026.6.35', '2026.9.4-beta.1')) {
        $sourceMetadata.packageVersion = $rejectedVersion
        $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"
        Assert-Fails -MessagePattern 'packageVersion' -Action {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $package -Architecture x64 -OutputDirectory $payload
        }
    }
    $sourceMetadata.packageVersion = '2026.9.4'

    $sourceMetadata.nodeVersion = '0.0.0'
    $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"
    Assert-Fails -MessagePattern 'does not match the source build' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package -Architecture x64 -OutputDirectory $payload
    }

    foreach ($architecture in @('x64', 'arm64')) {
        $wrongArchive = Join-Path $testRoot "node-v0.0.0-win-$architecture.zip"
        Set-Content -LiteralPath $wrongArchive -Value 'wrong runtime'
        $metadata.architecture = $architecture
        foreach ($version in @('v24.15.0', '26.1.0')) {
            $metadata.nodeVersion = $version
            $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
            $expectedName = "node-v$($version.TrimStart('v'))-win-$architecture.zip"
            Assert-Fails -MessagePattern ([regex]::Escape($expectedName)) -Action {
                & "$PSScriptRoot\Build-MSIX.ps1" `
                    -PayloadDirectory $payload -NodeArchivePath $wrongArchive `
                    -Architecture $architecture -PackageVersion '0.1.1.0' `
                    -SourceCommit ('1' * 40) -OutputDirectory "$testRoot\msix"
            }
        }
    }
    $metadata.nodeVersion = '>=24'
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
    Assert-Fails -MessagePattern 'Payload metadata is not valid' -Action {
        & "$PSScriptRoot\Build-MSIX.ps1" `
            -PayloadDirectory $payload -Architecture arm64 `
            -PackageVersion '0.1.1.0' -SourceCommit ('1' * 40) `
            -OutputDirectory "$testRoot\msix"
    }

    $metadata.nodeVersion = '24.20.0'
    $metadata.architecture = 'x64'
    $matchingArchive = Join-Path $testRoot 'node-v24.20.0-win-x64.zip'
    Set-Content -LiteralPath $matchingArchive -Value 'not reached'
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
    '{"name":"openclaw","version":"2026.6.35"}' |
        Set-Content -LiteralPath (Join-Path $payload 'app\package.json')
    Assert-Fails -MessagePattern 'application does not match the payload package version' -Action {
        & "$PSScriptRoot\Build-MSIX.ps1" `
            -PayloadDirectory $payload -NodeArchivePath $matchingArchive `
            -Architecture x64 -PackageVersion '0.1.1.0' -SourceCommit ('1' * 40) `
            -OutputDirectory "$testRoot\msix"
    }
    $legacyMetadata = $metadata |
        Select-Object * -ExcludeProperty channel, releaseTag, tagObject, resolvedAt, registryIntegrity
    $legacyMetadata.packageVersion = '2026.6.35'
    $legacyMetadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
    Assert-Fails -MessagePattern 'packageVersion' -Action {
        & "$PSScriptRoot\Build-MSIX.ps1" `
            -PayloadDirectory $payload -Architecture x64 -PackageVersion '0.1.1.0' `
            -SourceCommit ('1' * 40) -OutputDirectory "$testRoot\msix"
    }

    Write-Host 'Node.js source and packaging input tests passed.'
}
finally {
    $env:RUNNER_TEMP = $previousRunnerTemp
    $env:npm_config_cache = $previousNpmCache
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
