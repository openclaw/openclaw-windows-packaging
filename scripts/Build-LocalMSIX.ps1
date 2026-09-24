[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    [string]$PayloadDirectory,

    [long]$PayloadRunId,

    [switch]$RefreshPayload,

    [string]$NodeArchivePath,

    [string]$PackageVersion,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'LocalPackage.psm1') -Force

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

if (-not $PackageVersion) {
    $now = Get-Date
    $days = [int]($now.Date - [datetime]'2020-01-01').TotalDays
    $timeComponent = (($now.Hour * 3600) + ($now.Minute * 60) + $now.Second) %
        65535
    $PackageVersion = "0.1.$days.$timeComponent"
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path `
        $repositoryRoot `
        "artifacts\local-msix\$Architecture\$PackageVersion"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

if (Test-Path -LiteralPath $OutputDirectory) {
    throw (
        "The local output directory already exists: $OutputDirectory. " +
        'Choose another -PackageVersion or -OutputDirectory.'
    )
}

if ($NodeArchivePath) {
    $NodeArchivePath = (Resolve-Path -LiteralPath $NodeArchivePath).Path
}

$msixPath = Join-Path $OutputDirectory "OpenClawGateway-$Architecture.msix"
# LocalPackage.psm1 owns payload selection, its cache, and the checkout lock;
# it records a downloaded payload as the cached selection only after this
# composition succeeds.
$compose = {
    param([string]$resolvedPayloadDirectory)

    $payloadApplication = Join-Path $resolvedPayloadDirectory 'app'
    $payloadMetadata = Join-Path $resolvedPayloadDirectory 'payload-metadata.json'
    if (-not (Test-Path -LiteralPath $payloadApplication -PathType Container)) {
        throw "Required payload input was not found: $payloadApplication"
    }
    if (-not (Test-Path -LiteralPath $payloadMetadata -PathType Leaf)) {
        throw "Required payload input was not found: $payloadMetadata"
    }

    Push-Location $repositoryRoot
    try {
        Write-Host 'Restoring locked .NET dependencies.'
        Invoke-CheckedCommand `
            -FailureMessage 'Locked dependency restore failed.' `
            -Command {
                & dotnet restore `
                    .\src\OpenClaw.Launcher\OpenClaw.Launcher.csproj `
                    --runtime "win-$Architecture" `
                    -p:PublishAot=true `
                    -p:IncludePackagingContent=true `
                    "-p:Platform=$Architecture"
            }
        Invoke-CheckedCommand `
            -FailureMessage 'Session host dependency restore failed.' `
            -Command {
                & dotnet restore `
                    .\src\OpenClaw.SessionHost\OpenClaw.SessionHost.csproj `
                    --runtime "win-$Architecture" `
                    -p:PublishAot=true `
                    "-p:Platform=$Architecture"
            }

        $sourceCommit = (& git rev-parse HEAD) -join ''
        if ($LASTEXITCODE -ne 0 -or
            $sourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
            throw 'Unable to resolve the current source commit.'
        }
        $sourceTreeDirty = [bool](& git status --porcelain)
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to inspect the current source tree.'
        }

        Write-Host "Building unsigned MSIX version $PackageVersion."
        & .\scripts\Build-MSIX.ps1 `
            -PayloadDirectory $resolvedPayloadDirectory `
            -NodeArchivePath $NodeArchivePath `
            -Architecture $Architecture `
            -PackageVersion $PackageVersion `
            -SourceCommit $sourceCommit `
            -SourceTreeDirty:$sourceTreeDirty `
            -OutputDirectory $OutputDirectory
        if (-not (Test-Path -LiteralPath $msixPath -PathType Leaf)) {
            throw "The MSIX build did not produce $msixPath."
        }
    }
    finally {
        Pop-Location
    }
}
Invoke-LocalPackageMsixBuild `
    -RepositoryRoot $repositoryRoot `
    -Architecture $Architecture `
    -PayloadDirectory $PayloadDirectory `
    -PayloadRunId $PayloadRunId `
    -RefreshPayload:$RefreshPayload `
    -Compose $compose

Write-Host ''
Write-Host "Local MSIX is ready: $msixPath"
Write-Host (
    'Sign the package before installing it with Add-AppxPackage.'
)
