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
$repositoryRoot = Split-Path $PSScriptRoot -Parent

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
$nodeTarget = & node -p 'process.platform + "/" + process.arch'
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to determine the payload inspection Node.js platform and architecture.'
}
if (-not $IsWindows -or $nodeTarget -cnotmatch '^win32/(x64|arm64)$') {
    throw (
        "Payload runtime inspection requires win32/$Architecture Node.js; " +
        "found '$nodeTarget'. Build and inspect on the matching Windows " +
        'architecture. To cross-compose, ' +
        'supply an already-qualified payload to Build-MSIX.ps1 or ' +
        'Build-LocalMSIX.ps1 -PayloadDirectory instead.'
    )
}
$nodeArchitecture = $nodeTarget.Substring('win32/'.Length)
# npm's target CPU flag does not change process.arch inside dependency install scripts.
if ($nodeArchitecture -cne $Architecture) {
    throw (
        "Node.js architecture '$nodeArchitecture' does not match the '$Architecture' payload. " +
        "Run this build with $Architecture Node.js on a compatible Windows runner."
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
    nodeArchitecture = $nodeArchitecture
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

$applicationDirectory = Join-Path $OutputDirectory 'app'
if (Test-Path -LiteralPath $applicationDirectory) {
    Remove-Item -LiteralPath $applicationDirectory -Recurse -Force
}
Copy-Item `
    -LiteralPath $installedPackage `
    -Destination $applicationDirectory `
    -Recurse
$installedPackage = $applicationDirectory

$pluginSource = Join-Path $repositoryRoot 'plugins\gateway-isolation'
$pluginFiles = @('package.json', 'openclaw.plugin.json', 'index.js')
foreach ($pluginFile in $pluginFiles) {
    $sourcePath = Join-Path $pluginSource $pluginFile
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Gateway isolation plugin is missing required file: $sourcePath"
    }
}

$pluginManifest = Get-Content `
    -LiteralPath (Join-Path $pluginSource 'openclaw.plugin.json') `
    -Raw |
    ConvertFrom-Json
if (
    $pluginManifest.id -ne 'gateway-isolation' -or
    $pluginManifest.enabledByDefault -ne $true -or
    (
        $pluginManifest.PSObject.Properties.Name -contains
        'enabledByDefaultOnPlatforms' -and
        @($pluginManifest.enabledByDefaultOnPlatforms).Count -ne 0
    ) -or
    $pluginManifest.activation.onStartup -ne $true
) {
    throw 'Gateway isolation plugin manifest must enable bundled startup activation by default.'
}

$bundledPluginsDirectory = Join-Path $installedPackage 'dist\extensions'
if (-not (Test-Path -LiteralPath $bundledPluginsDirectory -PathType Container)) {
    throw (
        'The pinned OpenClaw payload does not expose the supported bundled ' +
        "plugin directory: $bundledPluginsDirectory"
    )
}
$pluginTarget = Join-Path $bundledPluginsDirectory 'gateway-isolation'
if (Test-Path -LiteralPath $pluginTarget) {
    throw (
        'The OpenClaw payload already contains a gateway-isolation plugin; ' +
        'refusing to replace upstream content.'
    )
}
New-Item -Path $pluginTarget -ItemType Directory | Out-Null
foreach ($pluginFile in $pluginFiles) {
    Copy-Item `
        -LiteralPath (Join-Path $pluginSource $pluginFile) `
        -Destination (Join-Path $pluginTarget $pluginFile)
}

$previousStateDirectory = $env:OPENCLAW_STATE_DIR
$previousConfigPath = $env:OPENCLAW_CONFIG_PATH
$previousIsolationMode = $env:CLAWCTL_GATEWAY_ISOLATION
$previousCacheHome = $env:XDG_CACHE_HOME
$validationStateDirectory = Join-Path `
    $stagingDirectory `
    'gateway-isolation-validation'
$validationConfigPath = Join-Path `
    $validationStateDirectory `
    'openclaw.json'
try {
    $env:OPENCLAW_STATE_DIR = $validationStateDirectory
    $env:OPENCLAW_CONFIG_PATH = $validationConfigPath
    $env:CLAWCTL_GATEWAY_ISOLATION = 'disabled'
    $env:XDG_CACHE_HOME = Join-Path $validationStateDirectory 'cache'
    Push-Location $installedPackage
    try {
        if (Test-Path -LiteralPath $validationConfigPath) {
            throw 'Gateway isolation validation requires a fresh, isolated profile.'
        }
        foreach ($profileKind in @('fresh', 'existing-without-plugin-decision')) {
            if ($profileKind -eq 'existing-without-plugin-decision') {
                New-Item -Path $validationStateDirectory -ItemType Directory -Force | Out-Null
                Set-Content -LiteralPath $validationConfigPath -Value '{}' -Encoding utf8
            }
            $configHashBefore = if (Test-Path -LiteralPath $validationConfigPath) {
                (Get-FileHash -LiteralPath $validationConfigPath -Algorithm SHA256).Hash
            } else { $null }
            $inspectionOutput = (
                & node `
                    .\openclaw.mjs `
                    plugins inspect gateway-isolation `
                    --runtime `
                    --json
            ) | Out-String
            if ($LASTEXITCODE -ne 0) {
                throw (
                    'The selected OpenClaw payload cannot load the Gateway ' +
                    "isolation plugin. Exit code: $LASTEXITCODE."
                )
            }
            $inspection = $inspectionOutput | ConvertFrom-Json
            if (
                $inspection.plugin.id -ne 'gateway-isolation' -or
                $inspection.plugin.origin -ne 'bundled' -or
                $inspection.plugin.enabled -ne $true -or
                $inspection.plugin.explicitlyEnabled -ne $false -or
                $inspection.plugin.activationSource -ne 'default' -or
                $inspection.plugin.activated -ne $true -or
                $inspection.plugin.status -ne 'loaded' -or
                $inspection.plugin.imported -ne $true -or
                $inspection.plugin.httpRoutes -ne 1 -or
                $inspection.httpRouteCount -ne 1 -or
                @($inspection.gatewayMethods).Count -ne 0 -or
                @($inspection.tools).Count -ne 0 -or
                @($inspection.services).Count -ne 0 -or
                @($inspection.diagnostics).Count -ne 0
            ) {
                throw (
                    'The selected OpenClaw payload must activate the bundled ' +
                    "Gateway isolation plugin by default ($profileKind), " +
                    'with the required read-only runtime shape.'
                )
            }
            $configHashAfter = if (Test-Path -LiteralPath $validationConfigPath) {
                (Get-FileHash -LiteralPath $validationConfigPath -Algorithm SHA256).Hash
            } else { $null }
            if ($configHashBefore -cne $configHashAfter) {
                throw 'Default plugin inspection must not create or change user configuration.'
            }
        }

        Set-Content `
            -LiteralPath $validationConfigPath `
            -Value '{"plugins":{"entries":{"gateway-isolation":{"enabled":false}}}}' `
            -Encoding utf8
        $disabledConfigHash = (Get-FileHash -LiteralPath $validationConfigPath -Algorithm SHA256).Hash
        $disabledInspectionOutput = (
            & node `
                .\openclaw.mjs `
                plugins inspect gateway-isolation `
                --runtime `
                --json
        ) | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw (
                'The selected OpenClaw payload cannot inspect an explicitly ' +
                "disabled Gateway isolation plugin. Exit code: $LASTEXITCODE."
            )
        }
        $disabledInspection = $disabledInspectionOutput | ConvertFrom-Json
        if (
            $disabledInspection.plugin.id -ne 'gateway-isolation' -or
            $disabledInspection.plugin.origin -ne 'bundled' -or
            $disabledInspection.plugin.enabled -ne $false -or
            $disabledInspection.plugin.activated -ne $false -or
            $disabledInspection.plugin.imported -ne $false -or
            $disabledInspection.plugin.httpRoutes -ne 0 -or
            $disabledInspection.httpRouteCount -ne 0 -or
            $disabledConfigHash -cne (
                Get-FileHash -LiteralPath $validationConfigPath -Algorithm SHA256
            ).Hash
        ) {
            throw 'An explicit user disable must remain unchanged and prevent plugin registration.'
        }

        & node .\openclaw.mjs --version
        if ($LASTEXITCODE -ne 0) {
            throw "OpenClaw payload smoke test failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
    Push-Location $installedPackage
    try {
        [string[]]$completionLines = @(
            & node .\openclaw.mjs completion --shell powershell
        )
        if ($LASTEXITCODE -ne 0) {
            throw (
                'The selected OpenClaw payload could not generate PowerShell ' +
                "completion. Exit code: $LASTEXITCODE."
            )
        }
    }
    finally {
        Pop-Location
    }

    $completionScript = [string]::Join("`n", $completionLines) + "`n"
    if (
        $completionScript -notmatch 'Register-ArgumentCompleter' -or
        $completionScript -notmatch '(?i)openclaw'
    ) {
        throw 'The selected OpenClaw payload generated an invalid PowerShell completion script.'
    }
    $completionDirectory = Join-Path $installedPackage 'shell-completions'
    New-Item -Path $completionDirectory -ItemType Directory | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $completionDirectory 'openclaw.ps1'),
        $completionScript,
        [Text.UTF8Encoding]::new($false)
    )
}
finally {
    $env:OPENCLAW_STATE_DIR = $previousStateDirectory
    $env:OPENCLAW_CONFIG_PATH = $previousConfigPath
    $env:CLAWCTL_GATEWAY_ISOLATION = $previousIsolationMode
    $env:XDG_CACHE_HOME = $previousCacheHome
    if (Test-Path -LiteralPath $validationStateDirectory) {
        Remove-Item `
            -LiteralPath $validationStateDirectory `
            -Recurse `
            -Force
    }
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
