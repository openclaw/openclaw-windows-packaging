[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Get-PayloadStartupFiles.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw-startup-files-$([guid]::NewGuid().ToString('N'))"
)

function Assert-Fails {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$MessagePattern
    )

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

function Write-FixtureFile {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$RelativePath,

        [string]$Content = ''
    )

    $path = Join-Path $Root $RelativePath
    New-Item -Path (Split-Path $path -Parent) -ItemType Directory -Force |
        Out-Null
    [IO.File]::WriteAllText($path, $Content)
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)

    $expectedText = @($Expected) -join "`n"
    $actualText = @($Actual) -join "`n"
    if ($expectedText -cne $actualText) {
        throw "$Message`nExpected:`n$expectedText`nActual:`n$actualText"
    }
}

try {
    $app = Join-Path $testRoot 'app'
    Write-FixtureFile $app 'openclaw.mjs' @'
#!/usr/bin/env node
import "./dist/entry.js";
import { readFile } from "node:fs/promises";
import path from "path";
const late = await import("./dist/late.js");
const computed = await import(`./dist/${name}.js`);
import "missing-optional";
import "https://example.invalid/module.js";
'@
    Write-FixtureFile $app 'dist\entry.js' @'
import { a } from "./chunk-a.js";
export * from "./chunk-b";
export { c } from "./dir";
import cjs from "cjs-pkg";
import { Type } from "@scope/exports-pkg/sub";
import "@scope/exports-pkg";
import "pattern-pkg/feature/x";
'@
    Write-FixtureFile $app 'dist\chunk-a.js' 'export const a = 1;'
    Write-FixtureFile $app 'dist\chunk-b.js' 'export const b = 1;'
    Write-FixtureFile $app 'dist\dir\index.js' 'export const c = 1;'
    Write-FixtureFile $app 'dist\late.js' 'export default 1;'
    Write-FixtureFile $app 'dist\unreached.js' 'export default 1;'
    Write-FixtureFile $app 'node_modules\cjs-pkg\package.json' '{"name":"cjs-pkg","main":"lib/main"}'
    Write-FixtureFile $app 'node_modules\cjs-pkg\lib\main.js' 'module.exports = require("./helper");'
    Write-FixtureFile $app 'node_modules\cjs-pkg\lib\helper.js' 'module.exports = 1;'
    Write-FixtureFile $app 'node_modules\@scope\exports-pkg\package.json' @'
{
  "name": "@scope/exports-pkg",
  "exports": {
    ".": { "types": "./types.d.ts", "import": "./esm/index.mjs", "require": "./cjs/index.cjs" },
    "./sub": { "browser": "./browser/sub.js", "default": "./esm/sub.mjs" },
    "./package.json": "./package.json"
  }
}
'@
    Write-FixtureFile $app 'node_modules\@scope\exports-pkg\esm\index.mjs' 'export {};'
    Write-FixtureFile $app 'node_modules\@scope\exports-pkg\esm\sub.mjs' 'export {};'
    Write-FixtureFile $app 'node_modules\@scope\exports-pkg\cjs\index.cjs' ''
    Write-FixtureFile $app 'node_modules\@scope\exports-pkg\browser\sub.js' ''
    Write-FixtureFile $app 'node_modules\pattern-pkg\package.json' '{"name":"pattern-pkg","exports":{"./feature/*":"./lib/*.js"}}'
    Write-FixtureFile $app 'node_modules\pattern-pkg\lib\x.js' 'export {};'
    Write-FixtureFile $app 'dist\extensions\beta\index.js' 'import "./runtime.js";'
    Write-FixtureFile $app 'dist\extensions\beta\runtime.js' ''
    Write-FixtureFile $app 'dist\extensions\alpha\index.js' 'import "../../chunk-a.js";'
    Write-FixtureFile $app 'dist\extensions\alpha\other.js' ''

    $expected = @(
        'openclaw.mjs'
        'dist/entry.js'
        'dist/late.js'
        'dist/chunk-a.js'
        'dist/chunk-b.js'
        'dist/dir/index.js'
        'node_modules/cjs-pkg/package.json'
        'node_modules/cjs-pkg/lib/main.js'
        'node_modules/@scope/exports-pkg/package.json'
        'node_modules/@scope/exports-pkg/esm/sub.mjs'
        'node_modules/@scope/exports-pkg/esm/index.mjs'
        'node_modules/pattern-pkg/package.json'
        'node_modules/pattern-pkg/lib/x.js'
        'node_modules/cjs-pkg/lib/helper.js'
        'dist/extensions/alpha/index.js'
        'dist/extensions/beta/index.js'
        'dist/extensions/beta/runtime.js'
    )
    $actual = @(& $scriptPath -ApplicationDirectory $app)
    Assert-Equal $expected $actual 'The startup list did not follow the expected import graph in breadth-first order.'

    $capped = @(& $scriptPath -ApplicationDirectory $app -MaximumFiles 3 -WarningVariable capWarnings -WarningAction SilentlyContinue)
    Assert-Equal $expected[0..2] $capped 'The capped list did not keep the files nearest the entry point.'
    if (@($capWarnings).Count -ne 1 -or "$($capWarnings[0])" -notmatch '3-file limit') {
        throw 'The capped list did not warn about truncation.'
    }

    $outside = Join-Path $testRoot 'outside.js'
    [IO.File]::WriteAllText($outside, 'export {};')
    $escapeApp = Join-Path $testRoot 'escape'
    Write-FixtureFile $escapeApp 'openclaw.mjs' 'import "../outside.js";'
    Assert-Equal @('openclaw.mjs') @(& $scriptPath -ApplicationDirectory $escapeApp) 'A specifier escaping the application directory was included.'

    $emptyApp = Join-Path $testRoot 'empty'
    New-Item -Path $emptyApp -ItemType Directory | Out-Null
    Assert-Fails -MessagePattern 'does not contain openclaw\.mjs' -Action {
        & $scriptPath -ApplicationDirectory $emptyApp
    }

    Write-Host 'Get-PayloadStartupFiles tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
