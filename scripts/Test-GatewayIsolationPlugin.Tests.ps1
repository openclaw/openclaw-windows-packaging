[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$pluginDirectory = Join-Path $repositoryRoot 'plugins\gateway-isolation'
$testRoot = Join-Path $env:TEMP (
    "openclaw-gateway-isolation-$([guid]::NewGuid().ToString('N'))"
)
$packageSource = Join-Path $testRoot 'package-source'
$packageDirectory = Join-Path $testRoot 'package'
$payloadDirectory = Join-Path $testRoot 'payload'
$fixtureArchitecture = & node -p 'process.arch'
if ($LASTEXITCODE -ne 0 -or $fixtureArchitecture -notin @('x64', 'arm64')) {
    throw 'The plugin payload fixture requires x64 or ARM64 Node.js.'
}

function Assert-Path {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected file was not found: $Path"
    }
}

try {
    $manifest = Get-Content `
        -LiteralPath (Join-Path $pluginDirectory 'openclaw.plugin.json') `
        -Raw |
        ConvertFrom-Json
    if (
        $manifest.id -ne 'gateway-isolation' -or
        $manifest.enabledByDefault -ne $true -or
        (
            $manifest.PSObject.Properties.Name -contains
            'enabledByDefaultOnPlatforms' -and
            @($manifest.enabledByDefaultOnPlatforms).Count -ne 0
        ) -or
        $manifest.activation.onStartup -ne $true -or
        $manifest.configSchema.additionalProperties -ne $false
    ) {
        throw 'Gateway isolation plugin manifest is not valid for default startup activation.'
    }

    $package = Get-Content `
        -LiteralPath (Join-Path $pluginDirectory 'package.json') `
        -Raw |
        ConvertFrom-Json
    if ($package.openclaw.extensions.Count -ne 1 -or
        $package.openclaw.extensions[0] -ne './index.js') {
        throw 'Gateway isolation plugin package has an invalid runtime entry.'
    }

    & node --test (Join-Path $pluginDirectory 'index.test.js')
    if ($LASTEXITCODE -ne 0) {
        throw "Gateway isolation plugin tests failed with exit code $LASTEXITCODE."
    }

    New-Item `
        -Path (
            Join-Path $packageSource 'dist\control-ui\assets'
        ), (
            Join-Path $packageSource 'dist\extensions\fixture'
        ), $packageDirectory `
        -ItemType Directory `
        -Force |
        Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $PSScriptRoot 'fixtures\openclaw-plugin-inspection.mjs') `
        -Destination (Join-Path $packageSource 'openclaw.mjs')
    Set-Content `
        -LiteralPath (Join-Path $packageSource 'dist\index.js') `
        -Value 'export {};' `
        -Encoding utf8
    Set-Content `
        -LiteralPath (Join-Path $packageSource 'dist\build-info.json') `
        -Value '{"buildId":"fixture-build"}' `
        -Encoding utf8
    Set-Content `
        -LiteralPath (Join-Path $packageSource 'dist\control-ui\sw.js') `
        -Value 'const EMBEDDED_CACHE_VERSION = "fixture-build";' `
        -Encoding utf8
    Set-Content `
        -LiteralPath (
            Join-Path $packageSource 'dist\control-ui\assets\app.js'
        ) `
        -Value 'const buildId = "fixture-build";' `
        -Encoding utf8
    Set-Content `
        -LiteralPath (
            Join-Path $packageSource 'dist\extensions\fixture\package.json'
        ) `
        -Value '{"name":"@openclaw/fixture","version":"1.0.0"}' `
        -Encoding utf8
    [ordered]@{
        name = 'openclaw'
        version = '2026.8.2'
        type = 'module'
    } |
        ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $packageSource 'package.json') `
            -Encoding utf8

    Push-Location $packageSource
    try {
        & npm pack --pack-destination $packageDirectory --silent
        if ($LASTEXITCODE -ne 0) {
            throw "npm pack failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    [ordered]@{
        repository = 'https://github.com/openclaw/openclaw'
        requestedRef = '0965053fe6b9341776df147a6934b7485c60b5ca'
        resolvedCommit = '0965053fe6b9341776df147a6934b7485c60b5ca'
        packageVersion = '2026.8.2'
        nodeVersion = (& node -p 'process.versions.node' | Out-String).Trim()
    } |
        ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $packageDirectory 'source.json') `
            -Encoding utf8

    $previousRunnerTemp = $env:RUNNER_TEMP
    $previousCacheHome = $env:XDG_CACHE_HOME
    $previousFailureFixture = $env:OPENCLAW_FIXTURE_FAIL_RUNTIME
    try {
        $env:RUNNER_TEMP = $testRoot
        $env:XDG_CACHE_HOME = Join-Path $testRoot 'caller-cache'
        $env:OPENCLAW_FIXTURE_FAIL_RUNTIME = $null
        & (Join-Path $PSScriptRoot 'Build-Payload.ps1') `
            -PackageDirectory $packageDirectory `
            -Architecture $fixtureArchitecture `
            -OutputDirectory $payloadDirectory
        if ($env:XDG_CACHE_HOME -cne (Join-Path $testRoot 'caller-cache') -or
            (Test-Path (Join-Path $testRoot 'caller-cache')) -or
            (Test-Path (Join-Path $testRoot "openclaw-stage-$fixtureArchitecture\gateway-isolation-validation"))) {
            throw 'Successful payload inspection did not isolate, restore and clean its cache.'
        }
        $env:OPENCLAW_FIXTURE_FAIL_RUNTIME = '1'
        $inspectionFailed = $false
        try {
            & (Join-Path $PSScriptRoot 'Build-Payload.ps1') `
                -PackageDirectory $packageDirectory `
                -Architecture $fixtureArchitecture `
                -OutputDirectory (Join-Path $testRoot 'failed-payload') `
                -ReuseStagedInstall
        }
        catch {
            if ($_.Exception.Message -notmatch 'cannot load the Gateway isolation plugin') {
                throw
            }
            if ($LASTEXITCODE -ne 1) {
                throw "Expected the runtime fixture to exit 1, received $LASTEXITCODE."
            }
            # The Actions pwsh wrapper propagates native exit codes, including expected failures.
            $global:LASTEXITCODE = 0
            $inspectionFailed = $true
        }
        if (-not $inspectionFailed -or
            $env:XDG_CACHE_HOME -cne (Join-Path $testRoot 'caller-cache') -or
            (Test-Path (Join-Path $testRoot "openclaw-stage-$fixtureArchitecture\gateway-isolation-validation"))) {
            throw 'Failed payload inspection must restore and remove only its isolated cache.'
        }
    }
    finally {
        $env:RUNNER_TEMP = $previousRunnerTemp
        $env:XDG_CACHE_HOME = $previousCacheHome
        $env:OPENCLAW_FIXTURE_FAIL_RUNTIME = $previousFailureFixture
    }

    $packagedPlugin = Join-Path `
        $payloadDirectory `
        'app\dist\extensions\gateway-isolation'
    foreach ($pluginFile in @('package.json', 'openclaw.plugin.json', 'index.js')) {
        Assert-Path -Path (Join-Path $packagedPlugin $pluginFile)
    }
    if (Test-Path -LiteralPath (Join-Path $packagedPlugin 'index.test.js')) {
        throw 'Plugin test sources must not be shipped in the MSIX payload.'
    }
    $stagedPlugin = Join-Path `
        $testRoot `
        "openclaw-stage-$fixtureArchitecture\node_modules\openclaw\dist\extensions\gateway-isolation"
    if (Test-Path -LiteralPath $stagedPlugin) {
        throw 'Plugin provisioning must not mutate the reusable staged install.'
    }

    $packagedManifest = Get-Content `
        -LiteralPath (Join-Path $packagedPlugin 'openclaw.plugin.json') `
        -Raw |
        ConvertFrom-Json
    if (
        $packagedManifest.id -ne 'gateway-isolation' -or
        $packagedManifest.enabledByDefault -ne $true -or
        (
            $packagedManifest.PSObject.Properties.Name -contains
            'enabledByDefaultOnPlatforms' -and
            @($packagedManifest.enabledByDefaultOnPlatforms).Count -ne 0
        )
    ) {
        throw 'Packaged Gateway isolation plugin manifest changed during provisioning.'
    }

    Write-Host 'Gateway isolation plugin tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
