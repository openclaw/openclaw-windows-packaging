[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    [string]$PayloadDirectory,

    [long]$PayloadRunId,

    [string]$PackageVersion,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$repository = 'openclaw/openclaw-windows-packaging'

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
$workDirectory = Join-Path $OutputDirectory 'work'

if (Test-Path -LiteralPath $OutputDirectory) {
    throw (
        "The local output directory already exists: $OutputDirectory. " +
        'Choose another -PackageVersion or -OutputDirectory.'
    )
}
New-Item -Path $workDirectory -ItemType Directory -Force | Out-Null

if ($PayloadDirectory) {
    $resolvedPayloadDirectory = (Resolve-Path -LiteralPath $PayloadDirectory).Path
}
else {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $gh) {
        throw 'GitHub CLI (gh) is required when -PayloadDirectory is omitted.'
    }

    if ($PayloadRunId -eq 0) {
        $runJson = & gh run list `
            --repo $repository `
            --workflow gateway-msix.yml `
            --branch main `
            --status success `
            --limit 1 `
            --json databaseId
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to query the latest successful payload workflow.'
        }

        $runs = @($runJson | ConvertFrom-Json)
        if ($runs.Count -ne 1) {
            throw 'No successful payload workflow was found.'
        }
        $PayloadRunId = $runs[0].databaseId
    }

    $resolvedPayloadDirectory = Join-Path $workDirectory 'payload'
    New-Item -Path $resolvedPayloadDirectory -ItemType Directory -Force |
        Out-Null
    Write-Host (
        "Downloading openclaw-gateway-payload-$Architecture from workflow $PayloadRunId."
    )
    Invoke-CheckedCommand `
        -FailureMessage 'Unable to download the payload artifact.' `
        -Command {
            & gh run download $PayloadRunId `
                --repo $repository `
                --name "openclaw-gateway-payload-$Architecture" `
                --dir $resolvedPayloadDirectory
        }
}

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
        -FailureMessage 'Locked guest-helper restore failed.' `
        -Command {
            & dotnet restore `
                .\src\OpenClaw.SessionHost\OpenClaw.SessionHost.csproj `
                --runtime "win-$Architecture" `
                -p:PublishAot=true `
                "-p:Platform=$Architecture" `
                "-p:RuntimeIdentifiers=win-$Architecture"
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

    Write-Host "Building MSIX version $PackageVersion."
    & .\scripts\Build-MSIX.ps1 `
        -PayloadDirectory $resolvedPayloadDirectory `
        -Architecture $Architecture `
        -PackageVersion $PackageVersion `
        -SourceCommit $sourceCommit `
        -SourceTreeDirty:$sourceTreeDirty `
        -OutputDirectory $OutputDirectory

    $msixPath = Join-Path $OutputDirectory "OpenClawGateway-$Architecture.msix"
    $metadataPath = Join-Path $OutputDirectory 'msix-metadata.json'
    $signingInputRoot = Join-Path $workDirectory 'signing-input'
    $signingInputArchitecture = Join-Path $signingInputRoot $Architecture
    $signingOutputRoot = Join-Path $workDirectory 'signed'
    New-Item `
        -Path $signingInputArchitecture `
        -ItemType Directory `
        -Force |
        Out-Null
    Copy-Item `
        -LiteralPath $msixPath `
        -Destination $signingInputArchitecture `
        -Force
    Copy-Item `
        -LiteralPath $metadataPath `
        -Destination $signingInputArchitecture `
        -Force

    Write-Host 'Applying a local test signature.'
    $publisher = [string](
        (
            Get-Content `
                -LiteralPath (Join-Path $repositoryRoot 'release-policy.json') `
                -Raw |
                ConvertFrom-Json
        ).publisher
    )
    $localSigningCertificate = Get-ChildItem `
        -Path 'Cert:\CurrentUser\My' `
        -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Subject -eq $publisher -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date) -and
            $_.FriendlyName -eq
                'OpenClaw Gateway reusable local MSIX test signing'
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    $signingParameters = @{}
    if ($null -ne $localSigningCertificate) {
        Write-Host (
            "Reusing local test-signing certificate " +
            "$($localSigningCertificate.Thumbprint)."
        )
        $signingParameters.CertificateThumbprint =
            $localSigningCertificate.Thumbprint
    }
    else {
        Write-Host 'Creating a reusable local test-signing certificate.'
        $signingParameters.KeepGeneratedCertificate = $true
        $signingParameters.CertificateValidityDays = 365
    }

    Invoke-CheckedCommand `
        -FailureMessage 'Test signing the local MSIX failed.' `
        -Command {
            & .\scripts\Sign-TestMSIX.ps1 `
                -ArtifactsDirectory $signingInputRoot `
                -OutputDirectory $signingOutputRoot `
                @signingParameters
        }

    $signedArchitectureDirectory = Join-Path $signingOutputRoot $Architecture
    Get-ChildItem -LiteralPath $signedArchitectureDirectory -File |
        Copy-Item -Destination $OutputDirectory -Force

    Remove-Item -LiteralPath $workDirectory -Recurse -Force
    Write-Host ''
    Write-Host "Local test-signed MSIX is ready: $msixPath"
    Write-Host (
        'Install it with .\scripts\Install-LocalMSIX.ps1 -PackageDirectory ' +
        "`"$OutputDirectory`"."
    )
}
finally {
    Pop-Location
}
