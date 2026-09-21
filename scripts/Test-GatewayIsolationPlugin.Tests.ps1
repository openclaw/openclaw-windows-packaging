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
        $manifest.enabledByDefault -ne $false -or
        (
            $manifest.PSObject.Properties.Name -contains
            'enabledByDefaultOnPlatforms' -and
            @($manifest.enabledByDefaultOnPlatforms).Count -ne 0
        ) -or
        $manifest.activation.onStartup -ne $true -or
        $manifest.configSchema.additionalProperties -ne $false
    ) {
        throw 'Gateway isolation plugin manifest is not valid for explicit enablement.'
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
    Set-Content `
        -LiteralPath (Join-Path $packageSource 'openclaw.mjs') `
        -Value @'
const args = process.argv.slice(2);
const fs = await import("node:fs");
const path = await import("node:path");
const configPath =
  process.env.OPENCLAW_CONFIG_PATH ??
  (process.env.OPENCLAW_STATE_DIR
    ? path.join(process.env.OPENCLAW_STATE_DIR, "openclaw.json")
    : undefined);
if (args[0] === "completion" && args[1] === "--shell" && args[2] === "powershell") {
  console.log("Register-ArgumentCompleter -Native -CommandName openclaw -ScriptBlock {}");
  process.exit(0);
}
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
          },
          anthropic: {
            config: {
              sessionCatalog: {
                enabled: false
              }
            }
          },
          codex: {
            config: {
              sessionCatalog: {
                enabled: false
              }
            }
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
'@ `
        -Encoding utf8
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

    $nodeArchitecture = & node -p 'process.arch'
    if ($LASTEXITCODE -ne 0 -or $nodeArchitecture -notin @('x64', 'arm64')) {
        throw 'Unable to determine the fixture Node.js architecture.'
    }
    $previousRunnerTemp = $env:RUNNER_TEMP
    try {
        $env:RUNNER_TEMP = $testRoot
        & (Join-Path $PSScriptRoot 'Build-Payload.ps1') `
            -PackageDirectory $packageDirectory `
            -Architecture $nodeArchitecture `
            -OutputDirectory $payloadDirectory
    }
    finally {
        $env:RUNNER_TEMP = $previousRunnerTemp
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
        "openclaw-stage-$nodeArchitecture\node_modules\openclaw\dist\extensions\gateway-isolation"
    if (Test-Path -LiteralPath $stagedPlugin) {
        throw 'Plugin provisioning must not mutate the reusable staged install.'
    }

    $packagedManifest = Get-Content `
        -LiteralPath (Join-Path $packagedPlugin 'openclaw.plugin.json') `
        -Raw |
        ConvertFrom-Json
    if (
        $packagedManifest.id -ne 'gateway-isolation' -or
        $packagedManifest.enabledByDefault -ne $false -or
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
