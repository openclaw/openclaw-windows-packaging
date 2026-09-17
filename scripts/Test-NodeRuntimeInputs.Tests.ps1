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
    New-Item `
        -ItemType Directory `
        -Path `
            "$source\dist\control-ui\assets", `
            "$source\dist\extensions\fixture", `
            $package `
        -Force |
        Out-Null
    '{"name":"openclaw","version":"0.0.0","type":"module"}' |
        Set-Content -LiteralPath "$source\package.json"
    @'
const fs = await import("node:fs");
const path = await import("node:path");
const args = process.argv.slice(2);
const configPath =
  process.env.OPENCLAW_CONFIG_PATH ??
  (process.env.OPENCLAW_STATE_DIR
    ? path.join(process.env.OPENCLAW_STATE_DIR, "openclaw.json")
    : undefined);
if (args[0] === "plugins" && args[1] === "enable") {
  if (!configPath) {
    throw new Error("Missing isolated validation configuration path.");
  }
  fs.mkdirSync(path.dirname(configPath), { recursive: true });
  fs.writeFileSync(
    configPath,
    JSON.stringify({
      plugins: {
        entries: {
          "gateway-isolation": {
            enabled: true
          }
        }
      }
    }),
  );
  process.exit(0);
}

const enabled = Boolean(configPath) && fs.existsSync(configPath) &&
  JSON.parse(fs.readFileSync(configPath, "utf8"))
    .plugins?.entries?.["gateway-isolation"]?.enabled === true;
const runtime = args.includes("--runtime");
console.log(JSON.stringify({
  plugin: {
    id: "gateway-isolation",
    origin: "bundled",
    enabled,
    activated: enabled && runtime,
    status: enabled && runtime ? "loaded" : "disabled",
    imported: enabled && runtime,
    httpRoutes: enabled && runtime ? 1 : 0
  },
  httpRouteCount: enabled && runtime ? 1 : 0,
  gatewayMethods: [],
  tools: [],
  services: [],
  diagnostics: []
}));
'@ | Set-Content -LiteralPath "$source\openclaw.mjs"
    'export {};' | Set-Content -LiteralPath "$source\dist\index.js"
    '{"name":"@openclaw/fixture","version":"1.0.0"}' |
        Set-Content `
            -LiteralPath "$source\dist\extensions\fixture\package.json"
    '{"buildId":"fixture-build"}' |
        Set-Content -LiteralPath "$source\dist\build-info.json"
    'const EMBEDDED_CACHE_VERSION = "fixture-build";' |
        Set-Content -LiteralPath "$source\dist\control-ui\sw.js"
    'const buildId = "fixture-build";' |
        Set-Content -LiteralPath "$source\dist\control-ui\assets\app.js"
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
        packageVersion = '0.0.0'
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

    $reusedPayload = Join-Path $testRoot 'payload-reused'
    & "$PSScriptRoot\Build-Payload.ps1" `
        -PackageDirectory $package `
        -Architecture x64 `
        -OutputDirectory $reusedPayload `
        -ReuseStagedInstall
    if (-not (Test-Path -LiteralPath "$reusedPayload\app\openclaw.mjs")) {
        throw 'The reused staged install did not produce an application payload.'
    }

    $packagePath = @(
        Get-ChildItem -LiteralPath $package -Filter '*.tgz' -File
    )[0].FullName
    $packageBytes = [IO.File]::ReadAllBytes($packagePath)
    Add-Content -LiteralPath $packagePath -Value 'changed package'
    Assert-Fails -MessagePattern 'does not match the requested packageSha256' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture x64 `
            -OutputDirectory (Join-Path $testRoot 'wrong-package-reuse') `
            -ReuseStagedInstall
    }
    [IO.File]::WriteAllBytes($packagePath, $packageBytes)

    $sourceMetadata.resolvedCommit = '2' * 40
    $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"
    Assert-Fails -MessagePattern 'does not match the requested resolvedCommit' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture x64 `
            -OutputDirectory (Join-Path $testRoot 'wrong-source-reuse') `
            -ReuseStagedInstall
    }
    $sourceMetadata.resolvedCommit = '1' * 40
    $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"

    $stagingMetadataPath = Join-Path (
        Join-Path $testRoot 'openclaw-stage-x64'
    ) '.openclaw-install.json'
    $stagingMetadata = Get-Content -LiteralPath $stagingMetadataPath -Raw
    Remove-Item -LiteralPath $stagingMetadataPath -Force
    Assert-Fails -MessagePattern 'missing provenance' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture x64 `
            -OutputDirectory (Join-Path $testRoot 'missing-provenance-reuse') `
            -ReuseStagedInstall
    }
    Set-Content -LiteralPath $stagingMetadataPath -Value '{'
    Assert-Fails -MessagePattern 'provenance is invalid' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture x64 `
            -OutputDirectory (Join-Path $testRoot 'invalid-provenance-reuse') `
            -ReuseStagedInstall
    }
    [IO.File]::WriteAllText(
        $stagingMetadataPath,
        $stagingMetadata,
        [Text.UTF8Encoding]::new($false)
    )

    Assert-Fails -MessagePattern 'staged OpenClaw install does not exist' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture arm64 `
            -OutputDirectory (Join-Path $testRoot 'missing-reuse') `
            -ReuseStagedInstall
    }

    $stagedPackage = Join-Path $testRoot 'openclaw-stage-x64\node_modules\openclaw'
    Set-Content -LiteralPath (Join-Path $stagedPackage 'node.exe') -Value 'unsafe'
    Assert-Fails -MessagePattern 'must not bundle Node.js' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture x64 `
            -OutputDirectory (Join-Path $testRoot 'unsafe-reuse') `
            -ReuseStagedInstall
    }
    Remove-Item -LiteralPath (Join-Path $stagedPackage 'node.exe') -Force

    'const EMBEDDED_CACHE_VERSION = "wrong-build";' |
        Set-Content -LiteralPath (
            Join-Path $stagedPackage 'dist\control-ui\sw.js'
        )
    Assert-Fails -MessagePattern 'build identity mismatch' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture x64 `
            -OutputDirectory (Join-Path $testRoot 'identity-reuse') `
            -ReuseStagedInstall
    }

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

    Write-Host 'Node.js source and packaging input tests passed.'
}
finally {
    $env:RUNNER_TEMP = $previousRunnerTemp
    $env:npm_config_cache = $previousNpmCache
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
