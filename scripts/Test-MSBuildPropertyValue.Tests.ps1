[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$policy = Get-Content `
    -LiteralPath (Join-Path $repositoryRoot 'release-policy.json') `
    -Raw |
    ConvertFrom-Json
$projectPath = Join-Path `
    $repositoryRoot `
    'src\OpenClaw.Launcher\OpenClaw.Launcher.csproj'

$publishers = @(
    [string]$policy.publisher
    [string]$policy.sideloadPackageIdentity.publisher
)
foreach ($publisher in $publishers) {
    if ([string]::IsNullOrWhiteSpace($publisher)) {
        throw 'Release policy contains an empty package publisher.'
    }

    $escapedPublisher = & (
        Join-Path $PSScriptRoot 'ConvertTo-MSBuildPropertyValue.ps1'
    ) -Value $publisher
    $resolvedPublisher = & dotnet msbuild $projectPath `
        -getProperty:PackageIdentityPublisher `
        "-p:PackageIdentityPublisher=$escapedPublisher" `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild rejected package publisher '$publisher'."
    }
    if ([string]$resolvedPublisher -cne $publisher) {
        throw (
            "MSBuild changed package publisher '$publisher' to " +
            "'$resolvedPublisher'."
        )
    }
}

Write-Host 'MSBuild package-publisher escaping tests passed.'
