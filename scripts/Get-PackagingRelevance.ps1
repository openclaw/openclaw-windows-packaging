[CmdletBinding(DefaultParameterSetName = 'FileList')]
param(
    [Parameter(Mandatory, ParameterSetName = 'FileList')]
    [string]$FileListPath,

    [Parameter(ParameterSetName = 'FileList')]
    [ValidateRange(1, 3000)]
    [int]$MaximumFiles = 3000,

    [Parameter(Mandatory, ParameterSetName = 'PathList')]
    [string]$PathListPath,

    [switch]$PayloadArtifact,

    [switch]$BundleBuild,

    [switch]$Versioning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# FileList preserves CI's GitHub JSON contract; PathList accepts local git -z output.
$documentationPathPattern = '^(?:.*\.md|docs/.*|LICENSE)$'
# A PR payload artifact is a branch-deployment contract, not an input to later
# workflow jobs. Publish it only when the branch can change the expanded app;
# every other local deployment can use the equivalent successful main payload.
$payloadArtifactPaths = @(
    '.github/workflows/gateway-msix.yml'
    'release-policy.json'
    'scripts/Build-Payload.ps1'
    'scripts/Copy-PayloadTree.ps1'
    'scripts/Get-WorkflowSource.ps1'
    'scripts/OpenClawSource.ps1'
)
# Bundles only add the architecture packages to one delivery archive. Rebuild
# them when composition or a child package's bundle-facing manifest can change;
# standalone MSIX composition proves every other package-content change.
$bundleBuildPaths = @(
    '.github/workflows/gateway-msix.yml'
    'scripts/Build-MSIX.ps1'
    'scripts/Build-MSIXBundle.ps1'
    'src/OpenClaw.Launcher/Package.appxmanifest'
)
$versioningPaths = @(
    '.github/workflows/gateway-msix.yml'
    'release-policy.json'
    'scripts/Sign-TestMSIX.ps1'
    'scripts/OpenClawSource.ps1'
    'scripts/Get-WorkflowSource.ps1'
    'scripts/Test-OpenClawSource.Tests.ps1'
    'scripts/Get-MSIXReleaseIdentity.ps1'
    'scripts/Get-MSIXUpgradeMatrix.ps1'
    'scripts/Test-MSIXReleaseIdentity.Tests.ps1'
    'scripts/Test-MSIXStoreUpgrade.ps1'
    'scripts/Test-MSIXUpgrade.ps1'
    'scripts/Test-MSIXUpgradeMatrix.Tests.ps1'
    'scripts/Test-WorkflowSigningConfiguration.ps1'
    'scripts/msix-upgrade-baselines.json'
)

$selectedModes = (
    [int][bool]$PayloadArtifact +
    [int][bool]$BundleBuild +
    [int][bool]$Versioning
)
if ($selectedModes -gt 1) {
    throw 'PayloadArtifact, BundleBuild, and Versioning are mutually exclusive.'
}

function Test-RelevantPath {
    param([string]$Path)

    if ($PayloadArtifact) {
        return (
            $Path -in $payloadArtifactPaths -or
            $Path.StartsWith(
                'plugins/gateway-isolation/',
                [StringComparison]::Ordinal
            )
        )
    }
    if ($BundleBuild) {
        return $Path -in $bundleBuildPaths
    }
    if ($Versioning) {
        return $Path -in $versioningPaths
    }
    return $Path -notmatch $documentationPathPattern
}

if ($PSCmdlet.ParameterSetName -eq 'PathList') {
    if (-not (Test-Path -LiteralPath $PathListPath -PathType Leaf)) {
        throw "Path list does not exist: $PathListPath"
    }

    $paths = @(
        [IO.File]::ReadAllText(
            $PathListPath,
            [Text.UTF8Encoding]::new($false)
        ).Split([char]0) |
            Where-Object { $_.Length -gt 0 }
    )
    foreach ($path in $paths) {
        if (Test-RelevantPath -Path $path) {
            return 'true'
        }
    }

    return 'false'
}

if (-not (Test-Path -LiteralPath $FileListPath -PathType Leaf)) {
    throw "Pull request file list does not exist: $FileListPath"
}

try {
    $pages = @(
        Get-Content -LiteralPath $FileListPath -Raw |
            ConvertFrom-Json
    )
}
catch {
    throw "Unable to parse the pull request file list: $($_.Exception.Message)"
}

$files = @(
    foreach ($page in $pages) {
        foreach ($file in @($page)) {
            if ($null -eq $file -or
                $file.PSObject.Properties.Name -notcontains 'filename' -or
                [string]::IsNullOrWhiteSpace([string]$file.filename)) {
                throw 'The pull request file list contains an invalid entry.'
            }
            $file
        }
    }
)
if ($files.Count -eq 0) {
    throw 'The pull request file list was empty.'
}

# GitHub returns at most 3,000 files. At the cap there is no proof that the
# response is complete, so conservatively run packaging.
if ($files.Count -ge $MaximumFiles) {
    return 'true'
}

foreach ($file in $files) {
    $previousFilename = if (
        $file.PSObject.Properties.Name -contains 'previous_filename'
    ) {
        [string]$file.previous_filename
    }
    else {
        ''
    }
    foreach ($path in @(
        [string]$file.filename
        $previousFilename
    )) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }
        if (Test-RelevantPath -Path $path) {
            return 'true'
        }
    }
}

'false'
