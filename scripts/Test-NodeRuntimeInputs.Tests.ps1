[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw-node-inputs-$([guid]::NewGuid().ToString('D'))"
)
$previousRunnerTemp = $env:RUNNER_TEMP
$previousNpmCache = $env:npm_config_cache
$previousNpmPrefix = $env:npm_config_prefix

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
    $ambientPrefix = Join-Path $testRoot 'ambient global prefix'
    New-Item -ItemType Directory -Path $ambientPrefix -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $ambientPrefix 'preserve.txt'), 'preserve')
    $env:npm_config_prefix = $ambientPrefix
    $source = Join-Path $testRoot 'source'
    $package = Join-Path $testRoot "package space #percent% [bracket]'quote"
    $payload = Join-Path $testRoot 'payload'
    New-Item `
        -ItemType Directory `
        -Path `
            "$source\dist\control-ui\assets", `
            "$source\dist\extensions\fixture\.setup", `
            $package `
        -Force |
        Out-Null
    $inventoryFixture = Join-Path $testRoot 'inventory fixture'
    $hiddenDirectory = Join-Path $inventoryFixture '.setup'
    New-Item -Path $hiddenDirectory -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $inventoryFixture 'b'), 'bb')
    [IO.File]::WriteAllText((Join-Path $hiddenDirectory 'a'), 'a')
    [IO.File]::SetAttributes($hiddenDirectory, [IO.FileAttributes]::Hidden)
    [IO.File]::SetAttributes((Join-Path $inventoryFixture 'b'), [IO.FileAttributes]::Hidden)
    $fixtureInventory = & "$PSScriptRoot\Get-PayloadInventory.ps1" `
        -ApplicationDirectory $inventoryFixture
    $expectedDigest = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes(".setup/a`t1`nb`t2`n"))).ToLowerInvariant()
    if ($fixtureInventory.algorithm -cne 'sha256-path-length-v1' -or
        $fixtureInventory.fileCount -ne 2 -or $fixtureInventory.sha256 -cne $expectedDigest) {
        throw 'The payload inventory must use sorted relative paths and lengths, including hidden entries.'
    }
    $dependencySource = Join-Path $testRoot 'dependency-source'
    $dependencyPackage = Join-Path $testRoot 'dependency-package'
    New-Item -ItemType Directory -Path $dependencySource, $dependencyPackage | Out-Null
    '{"name":"fixture-helper","version":"0.0.0","scripts":{"install":"node install.cjs"}}' |
        Set-Content -LiteralPath "$dependencySource\package.json"
    'require("node:fs").writeFileSync("dependency-hook.txt", "ran");' |
        Set-Content -LiteralPath "$dependencySource\install.cjs"
    & npm pack $dependencySource --ignore-scripts --offline --silent --pack-destination $dependencyPackage
    if ($LASTEXITCODE -ne 0) { throw 'Unable to pack the dependency lifecycle fixture.' }
    @{
        name = 'openclaw'
        version = '0.0.0'
        type = 'module'
        scripts = @{ install = 'node install.cjs' }
        dependencies = @{
            'fixture-helper' = "file:$(Join-Path $dependencyPackage 'fixture-helper-0.0.0.tgz')"
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$source\package.json"
    [IO.File]::WriteAllText("$source\.openclaw-lifecycle-pending", 'pending')
    @'
const fs = require("node:fs");
fs.writeFileSync("installed-architecture.txt", process.arch);
fs.writeFileSync("installed-directory.txt", __dirname);
fs.writeFileSync("installed-cache.txt", process.env.npm_config_cache);
fs.unlinkSync(".openclaw-lifecycle-pending");
'@ | Set-Content -LiteralPath "$source\install.cjs"
    Copy-Item `
        -LiteralPath (Join-Path $PSScriptRoot 'fixtures\openclaw-plugin-inspection.mjs') `
        -Destination "$source\openclaw.mjs"
    'export {};' | Set-Content -LiteralPath "$source\dist\index.js"
    'export {};' | Set-Content -LiteralPath "$source\dist\extensions\fixture\.setup\entry.mjs"
    '{"name":"@openclaw/fixture","version":"1.0.0"}' |
        Set-Content `
            -LiteralPath "$source\dist\extensions\fixture\package.json"
    '{"buildId":"fixture-build"}' |
        Set-Content -LiteralPath "$source\dist\build-info.json"
    'const EMBEDDED_CACHE_VERSION = "fixture-build";' |
        Set-Content -LiteralPath "$source\dist\control-ui\sw.js"
    'const buildId = "fixture-build";' |
        Set-Content -LiteralPath "$source\dist\control-ui\assets\app.js"
    '<html><head><script type="module" src="./assets/app.js"></script></head><body></body></html>' |
        Set-Content -LiteralPath "$source\dist\control-ui\index.html"
    & npm pack $source --ignore-scripts --offline --silent --pack-destination $package
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to pack the local Node.js input fixture.'
    }
    $nodeVersion = & node -p 'process.versions.node'
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to determine the fixture Node.js version.'
    }
    $nodeArchitecture = & node -p 'process.arch'
    if ($LASTEXITCODE -ne 0 -or $nodeArchitecture -notin @('x64', 'arm64')) {
        throw 'Unable to determine the fixture Node.js architecture.'
    }
    $otherArchitecture = if ($nodeArchitecture -eq 'x64') { 'arm64' } else { 'x64' }
    $actualNpmVersion = & npm --version
    if ($LASTEXITCODE -ne 0 -or $actualNpmVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw 'Unable to determine the fixture npm version.'
    }
    $sourceMetadata = @{
        repository = 'https://github.com/openclaw/openclaw'
        requestedRef = '1' * 40
        resolvedCommit = '1' * 40
        packageVersion = '0.0.0'
        nodeVersion = $nodeVersion
    }
    $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"

    $otherStage = Join-Path $testRoot "openclaw-stage-$otherArchitecture"
    New-Item -ItemType Directory -Path $otherStage | Out-Null
    $markerPath = Join-Path $otherStage 'preserve-existing-stage.txt'
    Set-Content -LiteralPath $markerPath -Value 'preserve'
    foreach ($reuse in @($false, $true)) {
        $wrongOutput = Join-Path $testRoot "wrong-node-architecture-$reuse"
        Assert-Fails -MessagePattern 'Node.js architecture.*does not match.*payload' -Action {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $package -Architecture $otherArchitecture `
                -OutputDirectory $wrongOutput -ReuseStagedInstall:$reuse
        }
        if ((Get-Content -LiteralPath $markerPath -Raw).Trim() -ne 'preserve' -or
            (Test-Path -LiteralPath $wrongOutput) -or
            (Test-Path -LiteralPath (Join-Path $otherStage 'node_modules'))) {
            throw 'A mismatched Node.js architecture changed staging or output before rejection.'
        }
    }

    $nodeExecutable = @(Get-Command node -CommandType Application)[0].Source
    $probeExitCode = 0
    $invalidTargetOutput = Join-Path $testRoot 'invalid-node-target'
    function node {
        if ($args.Count -eq 2 -and
            $args[0] -ceq '-p' -and
            $args[1] -ceq 'process.platform + "/" + process.arch') {
            $global:LASTEXITCODE = $probeExitCode
            $probeTarget
            return
        }
        & $nodeExecutable @args
    }
    try {
        foreach ($probeTarget in @("linux/$nodeArchitecture", 'unknown', 'win32/ia32')) {
            Assert-Fails -MessagePattern "requires win32/$nodeArchitecture Node.js" -Action {
                & "$PSScriptRoot\Build-Payload.ps1" `
                    -PackageDirectory $package -Architecture $nodeArchitecture `
                    -OutputDirectory $invalidTargetOutput
            }
        }
        $probeExitCode = 1
        Assert-Fails -MessagePattern 'Unable to determine.*platform and architecture' -Action {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $package -Architecture $nodeArchitecture `
                -OutputDirectory $invalidTargetOutput
        }
        if ((Test-Path $invalidTargetOutput) -or
            (Test-Path (Join-Path $testRoot "openclaw-stage-$nodeArchitecture"))) {
            throw 'An invalid or failed Node target probe must not produce a payload or stage.'
        }
    }
    finally {
        Remove-Item Function:\node
    }

    $currentStage = Join-Path $testRoot "openclaw-stage-$nodeArchitecture"
    New-Item -ItemType Directory -Path $currentStage | Out-Null
    $npmMarker = Join-Path $currentStage 'preserve-before-npm-check.txt'
    [IO.File]::WriteAllText($npmMarker, 'preserve')
    function npm {
        if ($args.Count -eq 1 -and $args[0] -ceq '--version') {
            $global:LASTEXITCODE = $npmProbe.exitCode
            $npmProbe.version
            return
        }
        throw 'A failed npm probe must not reach installation.'
    }
    try {
        foreach ($npmProbe in @(
            @{ version = 'invalid'; exitCode = 0; pattern = 'Unable to determine.*npm version' }
            @{ version = ''; exitCode = 0; pattern = 'Unable to determine.*npm version' }
            @{ version = '11.19.1'; exitCode = 1; pattern = 'Unable to determine.*npm version' }
        )) {
            foreach ($reuse in @($false, $true)) {
                $npmOutput = Join-Path $testRoot "failed-npm-$($npmProbe.version)-$reuse"
                Assert-Fails -MessagePattern $npmProbe.pattern -Action {
                    & "$PSScriptRoot\Build-Payload.ps1" `
                        -PackageDirectory $package -Architecture $nodeArchitecture `
                        -OutputDirectory $npmOutput -ReuseStagedInstall:$reuse
                }
                if ([IO.File]::ReadAllText($npmMarker) -cne 'preserve' -or
                    @(Get-ChildItem -LiteralPath $currentStage -Force).Count -ne 1 -or
                    (Test-Path -LiteralPath $npmOutput)) {
                    throw 'A failed npm probe changed staging or output before rejection.'
                }
            }
        }
        $commaPackage = Join-Path $testRoot 'package,blocked'
        New-Item -ItemType Directory -Path $commaPackage | Out-Null
        Copy-Item -LiteralPath (Get-ChildItem -LiteralPath $package -File).FullName -Destination $commaPackage
        $npmProbe = @{ version = '12.2.0'; exitCode = 0 }
        $commaOutput = Join-Path $testRoot 'comma-output'
        Assert-Fails -MessagePattern 'archive path without commas' -Action {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $commaPackage -Architecture $nodeArchitecture `
                -OutputDirectory $commaOutput
        }
        if ([IO.File]::ReadAllText($npmMarker) -cne 'preserve' -or
            @(Get-ChildItem -LiteralPath $currentStage -Force).Count -ne 1 -or
            (Test-Path -LiteralPath $commaOutput)) {
            throw 'An unrepresentable archive approval changed staging or output.'
        }
    }
    finally {
        Remove-Item Function:\npm
    }

    if ([version]$actualNpmVersion -lt [version]'12.0.0') {
        $npmCli = Join-Path (Split-Path $nodeExecutable -Parent) 'node_modules\npm\bin\npm-cli.js'
        $bundledNpmVersion = & $nodeExecutable $npmCli --version
        if ($LASTEXITCODE -ne 0 -or $bundledNpmVersion -cne $actualNpmVersion) {
            throw 'The legacy fixture requires the selected runtime bundled npm.'
        }
        $legacyOutput = Join-Path $testRoot 'legacy-npm-output'
        function npm {
            if ($args.Count -eq 1 -and $args[0] -ceq '--version') {
                $global:LASTEXITCODE = 0
                return '11.15.0'
            }
            if (@($args | Where-Object { $_ -like '--allow-scripts=*' -or
                $_ -like '--dangerously-allow-all-scripts=*' }).Count -ne 0) {
                throw 'Legacy npm received an unsupported lifecycle option.'
            }
            & $nodeExecutable $npmCli @args
        }
        try {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $package -Architecture $nodeArchitecture -OutputDirectory $legacyOutput
            if ([IO.File]::ReadAllText("$legacyOutput\app\installed-architecture.txt") -cne $nodeArchitecture) {
                throw 'The legacy unflagged install did not complete its lifecycle.'
            }
        }
        finally {
            Remove-Item Function:\npm
        }
    }

    $previousDangerousScripts = $env:npm_config_dangerously_allow_all_scripts
    try {
        $env:npm_config_dangerously_allow_all_scripts = 'true'
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package -Architecture $nodeArchitecture -OutputDirectory $payload
    }
    finally {
        $env:npm_config_dangerously_allow_all_scripts = $previousDangerousScripts
    }
    $dependencyMarker = Join-Path $payload 'app\node_modules\fixture-helper\dependency-hook.txt'
    if ((Test-Path -LiteralPath $dependencyMarker) -ne ([version]$actualNpmVersion -lt [version]'12.0.0')) {
        throw 'Dependency lifecycle behavior did not match upstream npm consumer policy.'
    }
    if ([IO.File]::ReadAllText((Join-Path $ambientPrefix 'preserve.txt')) -cne 'preserve' -or
        @(Get-ChildItem -LiteralPath $ambientPrefix -Force).Count -ne 1) {
        throw 'The payload install escaped its explicit staging prefix.'
    }
    $metadataPath = Join-Path $payload 'payload-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.nodeVersion -cne $nodeVersion) {
        throw 'The payload did not preserve the exact source build Node.js version.'
    }
    $setupPath = Join-Path $payload 'app\dist\extensions\fixture\.setup\entry.mjs'
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw 'The payload build dropped a dot-directory from the npm package.'
    }
    $inventory = & "$PSScriptRoot\Get-PayloadInventory.ps1" `
        -ApplicationDirectory (Join-Path $payload 'app')
    if ($metadata.inventory.fileCount -ne $inventory.fileCount -or
        $metadata.inventory.sha256 -cne $inventory.sha256) {
        throw 'The payload build did not record its final application inventory.'
    }
    Remove-Item -LiteralPath $setupPath
    $withoutSetup = & "$PSScriptRoot\Get-PayloadInventory.ps1" `
        -ApplicationDirectory (Join-Path $payload 'app')
    if ($withoutSetup.fileCount -ne $inventory.fileCount - 1 -or
        $withoutSetup.sha256 -ceq $inventory.sha256) {
        throw 'The payload inventory did not account for dot-directory contents.'
    }
    if ((Get-Content -LiteralPath "$payload\app\installed-architecture.txt" -Raw) -cne $nodeArchitecture) {
        throw 'The npm install lifecycle did not execute with the target Node.js architecture.'
    }
    $completionPath = Join-Path $payload 'app\shell-completions\openclaw.ps1'
    if (
        -not (Test-Path -LiteralPath $completionPath -PathType Leaf) -or
        (Get-Content -LiteralPath $completionPath -Raw) -notmatch
            'Register-ArgumentCompleter.+openclaw'
    ) {
        throw 'The payload did not include trusted OpenClaw PowerShell completion.'
    }

    $reusedPayload = Join-Path $testRoot 'payload-reused'
    & "$PSScriptRoot\Build-Payload.ps1" `
        -PackageDirectory $package `
        -Architecture $nodeArchitecture `
        -OutputDirectory $reusedPayload `
        -ReuseStagedInstall
    if (-not (Test-Path -LiteralPath "$reusedPayload\app\openclaw.mjs")) {
        throw 'The reused staged install did not produce an application payload.'
    }
    if ([IO.File]::ReadAllText("$reusedPayload\app\installed-directory.txt") -cne
        (Join-Path $testRoot "openclaw-stage-$nodeArchitecture\node_modules\openclaw") -or
        [IO.File]::ReadAllText("$reusedPayload\app\installed-cache.txt") -cne
        (Join-Path $testRoot "openclaw-npm-cache-$nodeArchitecture")) {
        throw 'Caller-provided RUNNER_TEMP must retain its staging and cache paths for reuse.'
    }
    if (
        -not (
            Test-Path `
                -LiteralPath "$reusedPayload\app\shell-completions\openclaw.ps1" `
                -PathType Leaf
        )
    ) {
        throw 'The reused staged install did not regenerate PowerShell completion.'
    }

    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    $previousRuntimeFailure = $env:OPENCLAW_FIXTURE_FAIL_RUNTIME
    $localTemp = Join-Path $testRoot 'local temp'
    New-Item -ItemType Directory -Path $localTemp | Out-Null
    try {
        $env:TEMP = $localTemp
        $env:TMP = $localTemp
        $env:RUNNER_TEMP = $null
        $env:OPENCLAW_FIXTURE_FAIL_RUNTIME = $null
        $localPayload = Join-Path $testRoot 'local-payload'
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package -Architecture $nodeArchitecture `
            -OutputDirectory $localPayload
        $installedDirectory = [IO.File]::ReadAllText("$localPayload\app\installed-directory.txt")
        $localRoot = Split-Path (Split-Path (Split-Path $installedDirectory -Parent) -Parent) -Parent
        $localCache = [IO.File]::ReadAllText("$localPayload\app\installed-cache.txt")
        if ((Split-Path $localRoot -Parent) -cne $localTemp -or
            (Split-Path $localRoot -Leaf) -notlike 'openclaw-payload-*' -or
            $localCache -cne (Join-Path $localRoot "openclaw-npm-cache-$nodeArchitecture") -or
            (Test-Path -LiteralPath $localRoot) -or
            -not [string]::IsNullOrEmpty($env:RUNNER_TEMP) -or
            -not (Test-Path -LiteralPath "$localPayload\payload-metadata.json")) {
            throw 'Local payload preparation must isolate and clean its own stage/cache without changing RUNNER_TEMP or removing its output.'
        }
        Assert-Fails -MessagePattern 'ReuseStagedInstall requires RUNNER_TEMP' -Action {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $package -Architecture $nodeArchitecture `
                -OutputDirectory (Join-Path $testRoot 'unowned-reuse') `
                -ReuseStagedInstall
        }
        $env:OPENCLAW_FIXTURE_FAIL_RUNTIME = '1'
        Assert-Fails -MessagePattern 'cannot load the Gateway isolation plugin' -Action {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $package -Architecture $nodeArchitecture `
                -OutputDirectory (Join-Path $testRoot 'failed-local-payload')
        }
        $global:LASTEXITCODE = 0
        if (@(Get-ChildItem -LiteralPath $localTemp -Directory -Filter 'openclaw-payload-*').Count -ne 0 -or
            -not (Test-Path -LiteralPath (Join-Path $currentStage '.openclaw-install.json')) -or
            [IO.File]::ReadAllText((Join-Path $ambientPrefix 'preserve.txt')) -cne 'preserve') {
            throw 'Failed local preparation must clean only its private root and preserve caller-owned reusable state.'
        }
    }
    finally {
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
        $env:RUNNER_TEMP = $testRoot
        $env:OPENCLAW_FIXTURE_FAIL_RUNTIME = $previousRuntimeFailure
    }

    $stagedPackage = Join-Path $currentStage 'node_modules\openclaw'
    foreach ($guard in @('.openclaw-lifecycle-pending', 'dist\openclaw-install-guard')) {
        $guardPath = Join-Path $stagedPackage $guard
        [IO.File]::WriteAllText($guardPath, 'pending')
        $guardOutput = Join-Path $testRoot ('guard-' + [IO.Path]::GetFileName($guard))
        Assert-Fails -MessagePattern 'install lifecycle did not complete' -Action {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $package -Architecture $nodeArchitecture `
                -OutputDirectory $guardOutput -ReuseStagedInstall
        }
        if (Test-Path -LiteralPath $guardOutput) { throw 'A pending lifecycle emitted a payload.' }
        Remove-Item -LiteralPath $guardPath
    }
    $previousIgnoreScripts = $env:npm_config_ignore_scripts
    try {
        $env:npm_config_ignore_scripts = 'true'
        $env:RUNNER_TEMP = Join-Path $testRoot 'blocked-lifecycle'
        New-Item -ItemType Directory -Path $env:RUNNER_TEMP | Out-Null
        $blockedOutput = Join-Path $testRoot 'blocked-lifecycle-output'
        Assert-Fails -MessagePattern 'install lifecycle did not complete' -Action {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $package -Architecture $nodeArchitecture `
                -OutputDirectory $blockedOutput
        }
        if ((Test-Path -LiteralPath $blockedOutput) -or
            (Test-Path -LiteralPath "$env:RUNNER_TEMP\openclaw-stage-$nodeArchitecture\.openclaw-install.json")) {
            throw 'A skipped install lifecycle recorded success or emitted output.'
        }
    }
    finally {
        $env:RUNNER_TEMP = $testRoot
        $env:npm_config_ignore_scripts = $previousIgnoreScripts
    }

    $packagePath = @(
        Get-ChildItem -LiteralPath $package -Filter '*.tgz' -File
    )[0].FullName
    $packageBytes = [IO.File]::ReadAllBytes($packagePath)
    Add-Content -LiteralPath $packagePath -Value 'changed package'
    Assert-Fails -MessagePattern 'does not match the requested packageSha256' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture $nodeArchitecture `
            -OutputDirectory (Join-Path $testRoot 'wrong-package-reuse') `
            -ReuseStagedInstall
    }
    [IO.File]::WriteAllBytes($packagePath, $packageBytes)

    $sourceMetadata.resolvedCommit = '2' * 40
    $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"
    Assert-Fails -MessagePattern 'does not match the requested resolvedCommit' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture $nodeArchitecture `
            -OutputDirectory (Join-Path $testRoot 'wrong-source-reuse') `
            -ReuseStagedInstall
    }
    $sourceMetadata.resolvedCommit = '1' * 40
    $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"

    $stagingMetadataPath = Join-Path (
        Join-Path $testRoot "openclaw-stage-$nodeArchitecture"
    ) '.openclaw-install.json'
    $stagingMetadata = Get-Content -LiteralPath $stagingMetadataPath -Raw
    $oldPolicyMetadata = $stagingMetadata | ConvertFrom-Json
    $oldPolicyMetadata.PSObject.Properties.Remove('installPolicy')
    $oldPolicyMetadata | ConvertTo-Json |
        Set-Content -LiteralPath $stagingMetadataPath -Encoding utf8
    Assert-Fails -MessagePattern 'does not match the requested installPolicy' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package -Architecture $nodeArchitecture `
            -OutputDirectory (Join-Path $testRoot 'legacy-install-policy') -ReuseStagedInstall
    }
    [IO.File]::WriteAllText($stagingMetadataPath, $stagingMetadata)
    $wrongArchitectureMetadata = $stagingMetadata | ConvertFrom-Json
    if ($wrongArchitectureMetadata.nodeArchitecture -cne $nodeArchitecture) {
        throw 'The staged install did not record its build Node.js architecture.'
    }
    $wrongArchitectureMetadata.nodeArchitecture = $otherArchitecture
    $wrongArchitectureMetadata | ConvertTo-Json |
        Set-Content -LiteralPath $stagingMetadataPath -Encoding utf8
    Assert-Fails -MessagePattern 'does not match the requested nodeArchitecture' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture $nodeArchitecture `
            -OutputDirectory (Join-Path $testRoot 'wrong-architecture-reuse') `
            -ReuseStagedInstall
    }
    Remove-Item -LiteralPath $stagingMetadataPath -Force
    Assert-Fails -MessagePattern 'missing provenance' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture $nodeArchitecture `
            -OutputDirectory (Join-Path $testRoot 'missing-provenance-reuse') `
            -ReuseStagedInstall
    }
    Set-Content -LiteralPath $stagingMetadataPath -Value '{'
    Assert-Fails -MessagePattern 'provenance is invalid' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture $nodeArchitecture `
            -OutputDirectory (Join-Path $testRoot 'invalid-provenance-reuse') `
            -ReuseStagedInstall
    }
    [IO.File]::WriteAllText(
        $stagingMetadataPath,
        $stagingMetadata,
        [Text.UTF8Encoding]::new($false)
    )

    $env:RUNNER_TEMP = Join-Path $testRoot 'missing-stage-root'
    try {
        Assert-Fails -MessagePattern 'staged OpenClaw install does not exist' -Action {
            & "$PSScriptRoot\Build-Payload.ps1" `
                -PackageDirectory $package `
                -Architecture $nodeArchitecture `
                -OutputDirectory (Join-Path $testRoot 'missing-reuse') `
                -ReuseStagedInstall
        }
    }
    finally {
        $env:RUNNER_TEMP = $testRoot
    }

    $stagedPackage = Join-Path $testRoot "openclaw-stage-$nodeArchitecture\node_modules\openclaw"
    Set-Content -LiteralPath (Join-Path $stagedPackage 'node.exe') -Value 'unsafe'
    Assert-Fails -MessagePattern 'must not bundle Node.js' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package `
            -Architecture $nodeArchitecture `
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
            -Architecture $nodeArchitecture `
            -OutputDirectory (Join-Path $testRoot 'identity-reuse') `
            -ReuseStagedInstall
    }

    $sourceMetadata.nodeVersion = '0.0.0'
    $sourceMetadata | ConvertTo-Json | Set-Content -LiteralPath "$package\source.json"
    Assert-Fails -MessagePattern 'does not match the source build' -Action {
        & "$PSScriptRoot\Build-Payload.ps1" `
            -PackageDirectory $package -Architecture $nodeArchitecture -OutputDirectory $payload
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
    $env:npm_config_prefix = $previousNpmPrefix
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
