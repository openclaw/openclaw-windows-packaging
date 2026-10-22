#Requires -Version 7.0
<#
.SYNOPSIS
    Stages the restored MXC SDK's redistribution notice and package metadata.
.DESCRIPTION
    PackageReference owns SDK acquisition and runtime assets. This operation
    never inspects native binaries. The MIT copyright/permission notice must
    accompany redistributions; package/version/architecture are descriptive.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [switch]$IncludePackagingContent
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$central = Get-Content -LiteralPath (Join-Path $root 'Directory.Packages.props') -Raw
$version = @($central.SelectNodes('//PackageVersion[@Include="Microsoft.Mxc.Sdk"]'))
if ($version.Count -ne 1) {
    throw 'The central MXC SDK package selection is ambiguous.'
}
if (-not $PackageDirectory) {
    $project = Join-Path $root 'src\OpenClaw.Launcher\OpenClaw.Launcher.csproj'
    $packaging = if ($IncludePackagingContent) { 'true' } else { 'false' }
    $lines = @(& dotnet msbuild $project -nologo -getProperty:PkgMicrosoft_Mxc_Sdk `
        "-p:IncludePackagingContent=$packaging" `
        "-p:RuntimeIdentifier=win-$Architecture" "-p:Platform=$Architecture")
    if ($LASTEXITCODE -ne 0) {
        throw 'Resolving the restored MXC SDK package failed.'
    }
    $PackageDirectory = [string]::Join("`n", [string[]]$lines).Trim()
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    throw 'Restore the launcher SDK package before staging its notice.'
}
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
[xml]$package = Get-Content -LiteralPath (
    Join-Path $PackageDirectory 'Microsoft.Mxc.Sdk.nuspec'
) -Raw
if ($package.package.metadata.id -cne 'Microsoft.Mxc.Sdk' -or
    $package.package.metadata.version -cne [string]$version[0].Version) {
    throw 'Restored SDK metadata does not match the central package selection.'
}
$license = Join-Path $PackageDirectory 'LICENSE.md'
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
    throw 'The SDK redistribution license is missing.'
}
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$notice = Join-Path $OutputDirectory 'mxc-LICENSE.md'
Copy-Item -LiteralPath $license -Destination $notice -Force
$provenance = [ordered]@{
    package = 'Microsoft.Mxc.Sdk'
    version = [string]$version[0].Version
    architecture = $Architecture
}
$path = Join-Path $OutputDirectory 'mxc-runtime.json'
[IO.File]::WriteAllText(
    $path,
    ($provenance | ConvertTo-Json) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    Provenance = [pscustomobject]$provenance
    Files = @($notice, $path)
}
