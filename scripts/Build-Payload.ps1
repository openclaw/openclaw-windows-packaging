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
    $pluginManifest.enabledByDefault -ne $false -or
    (
        $pluginManifest.PSObject.Properties.Name -contains
        'enabledByDefaultOnPlatforms' -and
        @($pluginManifest.enabledByDefaultOnPlatforms).Count -ne 0
    ) -or
    $pluginManifest.activation.onStartup -ne $true
) {
    throw 'Gateway isolation plugin manifest must be packaged but disabled by default.'
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
    Push-Location $installedPackage
    try {
        $defaultInspectionOutput = (
            & node `
                .\openclaw.mjs `
                plugins inspect gateway-isolation `
                --json
        ) | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw (
                'The selected OpenClaw payload cannot inspect the packaged ' +
                "Gateway isolation plugin. Exit code: $LASTEXITCODE."
            )
        }
        $defaultInspection = $defaultInspectionOutput | ConvertFrom-Json
        if (
            $defaultInspection.plugin.id -ne 'gateway-isolation' -or
            $defaultInspection.plugin.origin -ne 'bundled' -or
            $defaultInspection.plugin.enabled -ne $false -or
            $defaultInspection.plugin.activated -ne $false -or
            $defaultInspection.plugin.imported -ne $false -or
            $defaultInspection.plugin.httpRoutes -ne 0 -or
            $defaultInspection.httpRouteCount -ne 0
        ) {
            throw (
                'The packaged Gateway isolation plugin must remain disabled ' +
                'and unregistered until it is explicitly enabled.'
            )
        }

        & node .\openclaw.mjs plugins enable gateway-isolation
        if ($LASTEXITCODE -ne 0) {
            throw (
                'The selected OpenClaw payload cannot explicitly enable the ' +
                "Gateway isolation plugin in its isolated validation profile. " +
                "Exit code: $LASTEXITCODE."
            )
        }
        if (-not (
            Test-Path `
                -LiteralPath $validationConfigPath `
                -PathType Leaf
        )) {
            throw (
                'The selected OpenClaw payload did not create the isolated ' +
                'Gateway isolation validation configuration.'
            )
        }
        $validationConfig = Get-Content `
            -LiteralPath $validationConfigPath `
            -Raw |
            ConvertFrom-Json
        $validationEntryNames = @(
            $validationConfig.plugins.entries.PSObject.Properties.Name
        )
        if (
            $validationEntryNames.Count -ne 1 -or
            $validationEntryNames[0] -ne 'gateway-isolation' -or
            $validationConfig.plugins.entries.'gateway-isolation'.enabled -ne $true
        ) {
            throw (
                'The isolated validation configuration must explicitly enable ' +
                'only the Gateway isolation plugin.'
            )
        }

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
    }
    finally {
        Pop-Location
    }

    $inspection = $inspectionOutput | ConvertFrom-Json
    if (
        $inspection.plugin.id -ne 'gateway-isolation' -or
        $inspection.plugin.origin -ne 'bundled' -or
        $inspection.plugin.enabled -ne $true -or
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
            'The selected OpenClaw payload did not inspect the explicitly ' +
            'enabled Gateway isolation plugin with the required read-only ' +
            'runtime shape.'
        )
    }
}
finally {
    $env:OPENCLAW_STATE_DIR = $previousStateDirectory
    $env:OPENCLAW_CONFIG_PATH = $previousConfigPath
    $env:CLAWCTL_GATEWAY_ISOLATION = $previousIsolationMode
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
