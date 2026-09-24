[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$policyPath = Join-Path $repositoryRoot 'release-policy.json'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
if (
    [string]$policy.packageIdentityName -cne
        'OpenClawFoundation.OpenClawGateway' -or
    [string]$policy.packageFamilyName -cne
        'OpenClawFoundation.OpenClawGateway_rfcbke2p71se2' -or
    [string]$policy.publisher -cne
        'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC'
) {
    throw 'Release policy does not contain the Partner Center-reserved identity.'
}
[xml]$sourceManifest = Get-Content `
    -LiteralPath (Join-Path `
        $repositoryRoot `
        'src\OpenClaw.Launcher\Package.appxmanifest') `
    -Raw
if (
    [string]$sourceManifest.Package.Identity.Name -cne
        [string]$policy.packageIdentityName -or
    [string]$sourceManifest.Package.Identity.Publisher -cne
        [string]$policy.publisher
) {
    throw 'The source package manifest does not match release policy.'
}
$approvedCommit = [string]$policy.approvedCommit
$unapprovedCommit = 'b' * 40
$releaseIdentity = & (
    Join-Path $PSScriptRoot 'Get-MSIXReleaseIdentity.ps1'
) `
    -GatewayTag ([string]$policy.gatewayTag) `
    -MSIXRevision ([int]$policy.msixRevision)
$approvedPackageVersion = $releaseIdentity.PackageVersion
$approvedPayloadVersion = [string]$policy.payloadPackageVersion
$packagingCommit = '1111111111111111111111111111111111111111'
$suiteRoot = Join-Path $env:TEMP (
    "openclaw-signing-policy-$([guid]::NewGuid().ToString('N'))"
)
$canonicalRoot = Join-Path $suiteRoot 'canonical'
$testRoot = Join-Path $suiteRoot 'case'

function New-TestArtifact {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [ValidateSet('x64', 'arm64')]
        [string]$Architecture,

        [string]$PayloadCommit = $approvedCommit,

        [string]$PayloadPackageVersion = $approvedPayloadVersion,

        [bool]$SourceTreeDirty = $false,

        [string]$NodeRuntimeVersion = '24.16.0',

        [bool]$IncludeBundledNode = $false,

        [bool]$IncludeApplicationBundledNode = $false,

        [bool]$IncludeApplicationNodeArchive = $false,

        [bool]$IncludeEncodedScopedDependency = $false
    )

    $directory = Join-Path $Root $Architecture
    $staging = Join-Path $Root ".$Architecture-package"
    $payloadDirectory = Join-Path $staging 'payload'
    $applicationDirectory = Join-Path $staging 'app'
    New-Item `
        -Path $directory, $payloadDirectory, $applicationDirectory `
        -ItemType Directory `
        -Force |
        Out-Null

    Set-Content `
        -LiteralPath (Join-Path $applicationDirectory 'openclaw.mjs') `
        -Value "payload-$Architecture"
    if ($IncludeApplicationBundledNode) {
        Set-Content `
            -LiteralPath (Join-Path $applicationDirectory 'node.exe') `
            -Value 'bundled-node'
    }
    if ($IncludeApplicationNodeArchive) {
        Set-Content `
            -LiteralPath (
                Join-Path $applicationDirectory 'node-v24.16.0-win-x64.7z'
            ) `
            -Value 'bundled-node-archive'
    }
    if ($IncludeEncodedScopedDependency) {
        $scopedDependencyDirectory = Join-Path `
            $applicationDirectory `
            'node_modules\%40scope'
        New-Item `
            -Path $scopedDependencyDirectory `
            -ItemType Directory `
            -Force |
            Out-Null
        Set-Content `
            -LiteralPath (
                Join-Path $scopedDependencyDirectory 'package.json'
            ) `
            -Value '{"name":"@scope/package"}'
    }

    $payloadFiles = @(
        Get-ChildItem -LiteralPath $applicationDirectory -File -Recurse |
            ForEach-Object {
                [ordered]@{
                    path = (
                        [IO.Path]::GetRelativePath(
                            $applicationDirectory,
                            $_.FullName
                        )
                    ).Replace('\', '/').Replace('%40', '@')
                    length = $_.Length
                    sha256 = (
                        Get-FileHash `
                            -LiteralPath $_.FullName `
                            -Algorithm SHA256
                    ).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object path
    )
    [ordered]@{
        files = $payloadFiles
    } |
        # Keep the nested inventory file records intact.
        ConvertTo-Json -Depth 4 |
        Set-Content `
            -LiteralPath (Join-Path $payloadDirectory 'payload-files.json') `
            -Encoding utf8

    @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="$($policy.packageIdentityName)"
            Publisher="$($policy.publisher)"
            Version="$approvedPackageVersion"
            ProcessorArchitecture="$Architecture" />
</Package>
"@ | Set-Content `
        -LiteralPath (Join-Path $staging 'AppxManifest.xml') `
        -Encoding utf8

    if ($IncludeBundledNode) {
        $runtimeDirectory = Join-Path $staging 'runtime'
        New-Item -Path $runtimeDirectory -ItemType Directory | Out-Null
        Set-Content `
            -LiteralPath (Join-Path $runtimeDirectory 'node.exe') `
            -Value 'bundled-node'
    }
    else {
        $runtimeDirectory = Join-Path $staging 'runtime'
        New-Item -Path $runtimeDirectory -ItemType Directory | Out-Null
    }
    $nodeRuntimeArchive =
        "node-v$nodeRuntimeVersion-win-$Architecture.zip"
    $nodeRuntimePath = Join-Path $runtimeDirectory $nodeRuntimeArchive
    $nodeRuntimeZip = [IO.Compression.ZipFile]::Open(
        $nodeRuntimePath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        $nodeRuntimeRoot = [IO.Path]::GetFileNameWithoutExtension(
            $nodeRuntimeArchive
        )
        $nodeEntry = $nodeRuntimeZip.CreateEntry(
            "$nodeRuntimeRoot/node.exe"
        )
        $nodeWriter = [IO.StreamWriter]::new($nodeEntry.Open())
        try {
            $nodeWriter.Write('bundled-node')
        }
        finally {
            $nodeWriter.Dispose()
        }
    }
    finally {
        $nodeRuntimeZip.Dispose()
    }
    $nodeRuntimeHash = (
        Get-FileHash -LiteralPath $nodeRuntimePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()

    $mxcRuntimeVersion = '0.8.0'
    $mxcRuntimeDirectory = Join-Path $staging "mxc\$Architecture"
    New-Item -Path $mxcRuntimeDirectory -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $mxcRuntimeDirectory 'wxc-exec.exe'),
        "executor-$Architecture")
    [IO.File]::WriteAllText(
        (Join-Path $mxcRuntimeDirectory 'plm.exe'),
        "plm-$Architecture")
    [IO.File]::WriteAllText(
        (Join-Path $mxcRuntimeDirectory 'LICENSE.md'),
        'fixture license')
    [IO.File]::WriteAllText(
        (Join-Path $mxcRuntimeDirectory 'mxc-runtime.json'),
        "{`"version`":`"$mxcRuntimeVersion`",`"architecture`":`"$Architecture`"}")
    $mxcRuntimeFiles = @(
        Get-ChildItem -LiteralPath $mxcRuntimeDirectory -File -Recurse |
            ForEach-Object {
                [ordered]@{
                    path = (
                        [IO.Path]::GetRelativePath(
                            $mxcRuntimeDirectory,
                            $_.FullName
                        )
                    ).Replace('\', '/')
                    length = $_.Length
                    sha256 = (
                        Get-FileHash `
                            -LiteralPath $_.FullName `
                            -Algorithm SHA256
                    ).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object path
    )

    $sessionHostDirectory = Join-Path $staging "session-host\$Architecture"
    New-Item -Path $sessionHostDirectory -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $sessionHostDirectory 'openclaw-session-host.exe'),
        "session-host-$Architecture")
    $sessionHostFiles = @(
        Get-ChildItem -LiteralPath $sessionHostDirectory -File -Recurse |
            ForEach-Object {
                [ordered]@{
                    path = (
                        [IO.Path]::GetRelativePath(
                            $sessionHostDirectory,
                            $_.FullName
                        )
                    ).Replace('\', '/')
                    length = $_.Length
                    sha256 = (
                        Get-FileHash `
                            -LiteralPath $_.FullName `
                            -Algorithm SHA256
                    ).Hash.ToLowerInvariant()
                }
            }
    )

    $msixName = "OpenClawGateway-$Architecture.msix"
    $msixPath = Join-Path $directory $msixName
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $msixPath)
    Remove-Item -LiteralPath $staging -Recurse -Force

    $msixHash = (
        Get-FileHash -LiteralPath $msixPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    [ordered]@{
        packagingRepository =
            'https://github.com/openclaw/openclaw-windows-packaging'
        packagingCommit = $packagingCommit
        sourceTreeDirty = $SourceTreeDirty
        payloadRepository = $policy.repository
        payloadRequestedRef = $PayloadCommit
        payloadResolvedCommit = $PayloadCommit
        payloadPackageVersion = $PayloadPackageVersion
        payloadLayout = 'immutable-package'
        payloadFileCount = $payloadFiles.Count
        nodeRuntimeVersion = $nodeRuntimeVersion
        nodeRuntimeArchive = $nodeRuntimeArchive
        nodeRuntimeSha256 = $nodeRuntimeHash
        mxcRuntimeVersion = $mxcRuntimeVersion
        mxcRuntimeFiles = $mxcRuntimeFiles
        sessionHostFiles = $sessionHostFiles
        architecture = $Architecture
        archive = $msixName
        sha256 = $msixHash
        signed = $false
        packageVersion = $approvedPackageVersion
        packageIdentityName = $policy.packageIdentityName
        packageFamilyName = $policy.packageFamilyName
        publisher = $policy.publisher
    } |
        ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $directory 'msix-metadata.json') `
            -Encoding utf8
}

function Invoke-PolicyValidation {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [string]$RequestedRef = $approvedCommit,

        [switch]$PreserveBundle
    )

    if (-not $PreserveBundle) {
        $x64Hash = (Get-FileHash `
            -LiteralPath (Join-Path $Root 'x64\OpenClawGateway-x64.msix') `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        $arm64Hash = (Get-FileHash `
            -LiteralPath (Join-Path $Root 'arm64\OpenClawGateway-arm64.msix') `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        if (
            $x64Hash -ceq $canonicalDefaultHashes.x64 -and
            $arm64Hash -ceq $canonicalDefaultHashes.arm64
        ) {
            New-Item -Path (Join-Path $Root 'bundle') -ItemType Directory -Force |
                Out-Null
            Copy-Item `
                -LiteralPath (Join-Path `
                    $canonicalDefaultRoot `
                    'bundle\OpenClawGateway.msixbundle') `
                -Destination (Join-Path $Root 'bundle\OpenClawGateway.msixbundle') `
                -Force
        }
        else {
            New-TestBundle -Root $Root
        }
    }

    & (Join-Path $PSScriptRoot 'Test-SigningInputs.ps1') `
        -ArtifactsDirectory $Root `
        -PolicyPath $policyPath `
        -BundlePath (Join-Path $Root 'bundle\OpenClawGateway.msixbundle') `
        -RequestedRef $RequestedRef `
        -PackagingCommit $packagingCommit
}

function New-TestBundle {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [string]$X64Package = (Join-Path `
            $Root `
            'x64\OpenClawGateway-x64.msix'),

        [string]$BundleVersion = $approvedPackageVersion
    )

    $bundleDirectory = Join-Path $Root 'bundle'
    $bundleStaging = Join-Path $Root '.bundle-package'
    Remove-Item `
        -LiteralPath $bundleDirectory, $bundleStaging `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue
    $bundleMetadata = Join-Path $bundleStaging 'AppxMetadata'
    New-Item `
        -Path $bundleDirectory, $bundleMetadata `
        -ItemType Directory `
        -Force |
        Out-Null

    Copy-Item `
        -LiteralPath $X64Package `
        -Destination (Join-Path $bundleStaging 'OpenClawGateway-x64.msix')
    Copy-Item `
        -LiteralPath (Join-Path `
            $Root `
            'arm64\OpenClawGateway-arm64.msix') `
        -Destination (Join-Path $bundleStaging 'OpenClawGateway-arm64.msix')

    @"
<?xml version="1.0" encoding="utf-8"?>
<Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle">
  <Identity Name="$($policy.packageIdentityName)"
            Publisher="$($policy.publisher)"
            Version="$BundleVersion" />
  <Packages>
    <Package Type="application"
             Version="$approvedPackageVersion"
             Architecture="x64"
             FileName="OpenClawGateway-x64.msix" />
    <Package Type="application"
             Version="$approvedPackageVersion"
             Architecture="arm64"
             FileName="OpenClawGateway-arm64.msix" />
  </Packages>
</Bundle>
"@ | Set-Content `
        -LiteralPath (Join-Path `
            $bundleMetadata `
            'AppxBundleManifest.xml') `
        -Encoding utf8

    [IO.Compression.ZipFile]::CreateFromDirectory(
        $bundleStaging,
        (Join-Path $bundleDirectory 'OpenClawGateway.msixbundle')
    )
    Remove-Item -LiteralPath $bundleStaging -Recurse -Force
}

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
            throw (
                "Expected failure matching '$MessagePattern'; received: " +
                $_.Exception.Message
            )
        }
        return
    }

    throw "Expected failure matching '$MessagePattern', but the action succeeded."
}

function Update-TestMsix {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [ValidateSet('x64', 'arm64')]
        [string]$Architecture,

        [Parameter(Mandatory)]
        [scriptblock]$Mutator
    )

    $msixPath = Join-Path `
        $Root `
        "$Architecture\OpenClawGateway-$Architecture.msix"
    $expanded = Join-Path $Root ".$Architecture-mutated"
    [IO.Compression.ZipFile]::ExtractToDirectory($msixPath, $expanded)
    try {
        & $Mutator $expanded
        Remove-Item -LiteralPath $msixPath
        [IO.Compression.ZipFile]::CreateFromDirectory($expanded, $msixPath)
    }
    finally {
        Remove-Item `
            -LiteralPath $expanded `
            -Recurse `
            -Force `
            -ErrorAction SilentlyContinue
    }

    $metadataPath = Join-Path $Root "$Architecture\msix-metadata.json"
    $metadata = Get-Content -LiteralPath $metadataPath -Raw |
        ConvertFrom-Json
    $metadata.sha256 = (
        Get-FileHash -LiteralPath $msixPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $metadata |
        ConvertTo-Json |
        Set-Content -LiteralPath $metadataPath -Encoding utf8
}

# Canonical artifacts are built once and copied into each case. Recording
# their hashes lets the suite prove at the end that no case mutated them.
$canonicalFileHashes = @{}

function Register-CanonicalFiles {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse) {
        $canonicalFileHashes[$file.FullName] = (
            Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        ).Hash
    }
}

function Copy-TestArtifact {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [ValidateSet('x64', 'arm64')]
        [string]$Architecture,

        [string]$PayloadCommit = $approvedCommit,

        [string]$PayloadPackageVersion = $approvedPayloadVersion,

        [bool]$SourceTreeDirty = $false,

        [string]$NodeRuntimeVersion = '24.16.0',

        [bool]$IncludeBundledNode = $false,

        [bool]$IncludeApplicationBundledNode = $false,

        [bool]$IncludeApplicationNodeArchive = $false,

        [bool]$IncludeEncodedScopedDependency = $false
    )

    $keyInputs = @(
        $Architecture,
        $PayloadCommit,
        $PayloadPackageVersion,
        $SourceTreeDirty,
        $NodeRuntimeVersion,
        $IncludeBundledNode,
        $IncludeApplicationBundledNode,
        $IncludeApplicationNodeArchive,
        $IncludeEncodedScopedDependency
    ) -join '|'
    $keyBytes = [Text.Encoding]::UTF8.GetBytes($keyInputs)
    $keyHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($keyBytes)
    ).ToLowerInvariant()
    $canonicalVariantRoot = Join-Path $canonicalRoot "$Architecture-$keyHash"
    $canonicalArtifact = Join-Path $canonicalVariantRoot $Architecture
    if (-not (Test-Path -LiteralPath $canonicalArtifact -PathType Container)) {
        New-TestArtifact `
            -Root $canonicalVariantRoot `
            -Architecture $Architecture `
            -PayloadCommit $PayloadCommit `
            -PayloadPackageVersion $PayloadPackageVersion `
            -SourceTreeDirty $SourceTreeDirty `
            -NodeRuntimeVersion $NodeRuntimeVersion `
            -IncludeBundledNode $IncludeBundledNode `
            -IncludeApplicationBundledNode $IncludeApplicationBundledNode `
            -IncludeApplicationNodeArchive $IncludeApplicationNodeArchive `
            -IncludeEncodedScopedDependency $IncludeEncodedScopedDependency
        Register-CanonicalFiles -Path $canonicalArtifact
    }

    $caseArtifact = Join-Path $Root $Architecture
    Remove-Item `
        -LiteralPath $caseArtifact `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue
    New-Item -Path $Root -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath $canonicalArtifact -Destination $caseArtifact -Recurse
}

function Reset-TestArtifacts {
    param([string]$PayloadCommit = $approvedCommit)

    Remove-Item `
        -LiteralPath $testRoot `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -PayloadCommit $PayloadCommit
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture arm64 `
        -PayloadCommit $PayloadCommit
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $canonicalDefaultRoot = Join-Path $canonicalRoot 'default'
    Copy-TestArtifact -Root $canonicalDefaultRoot -Architecture x64
    Copy-TestArtifact -Root $canonicalDefaultRoot -Architecture arm64
    New-TestBundle -Root $canonicalDefaultRoot
    Register-CanonicalFiles -Path $canonicalDefaultRoot
    $canonicalDefaultHashes = @{
        x64 = (Get-FileHash `
            -LiteralPath (Join-Path `
                $canonicalDefaultRoot 'x64\OpenClawGateway-x64.msix') `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        arm64 = (Get-FileHash `
            -LiteralPath (Join-Path `
                $canonicalDefaultRoot 'arm64\OpenClawGateway-arm64.msix') `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    Reset-TestArtifacts
    Invoke-PolicyValidation -Root $testRoot

    Reset-TestArtifacts
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -NodeRuntimeVersion '26.1.0'
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture arm64 `
        -NodeRuntimeVersion '26.1.0'
    Invoke-PolicyValidation -Root $testRoot

    Reset-TestArtifacts
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture arm64 `
        -NodeRuntimeVersion '26.1.0'
    Assert-Fails `
        -MessagePattern 'Node.js runtime versions do not match' `
        -Action { Invoke-PolicyValidation -Root $testRoot }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        $manifestPath = Join-Path $Expanded 'AppxManifest.xml'
        [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
        $manifest.Package.Identity.Name = 'OpenClaw.Gateway'
        $manifest.Save($manifestPath)
    }
    Assert-Fails `
        -MessagePattern 'manifest identity is unexpected' `
        -Action { Invoke-PolicyValidation -Root $testRoot }

    Reset-TestArtifacts
    $x64MetadataPath = Join-Path $testRoot 'x64\msix-metadata.json'
    $x64Metadata = Get-Content -LiteralPath $x64MetadataPath -Raw |
        ConvertFrom-Json
    $x64Metadata.packageFamilyName = 'OpenClaw.Gateway_kaa03rpbbqef6'
    $x64Metadata |
        ConvertTo-Json |
        Set-Content -LiteralPath $x64MetadataPath -Encoding utf8
    Assert-Fails `
        -MessagePattern 'metadata is not eligible' `
        -Action { Invoke-PolicyValidation -Root $testRoot }

    Reset-TestArtifacts
    New-TestBundle -Root $testRoot -BundleVersion '2026.8.2001.0'
    Assert-Fails `
        -MessagePattern 'bundle manifest identity is unexpected' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot -PreserveBundle
        }

    Reset-TestArtifacts
    Assert-Fails `
        -MessagePattern 'approved immutable OpenClaw commit' `
        -Action {
            Invoke-PolicyValidation `
                -Root $testRoot `
                -RequestedRef $unapprovedCommit
        }
    Reset-TestArtifacts -PayloadCommit $unapprovedCommit
    Assert-Fails `
        -MessagePattern 'MSIX metadata is not eligible for signing' `
        -Action { Invoke-PolicyValidation -Root $testRoot }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Set-Content `
            -LiteralPath (Join-Path $Expanded 'mxc\x64\wxc-exec.exe') `
            -Value 'tampered' `
            -Encoding utf8
    }
    Assert-Fails `
        -MessagePattern 'MXC runtime file is invalid' `
        -Action { Invoke-PolicyValidation -Root $testRoot }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Remove-Item -LiteralPath (
            Join-Path $Expanded 'mxc\x64\mxc-runtime.json'
        )
    }
    Assert-Fails `
        -MessagePattern "Expected one 'mxc/x64/mxc-runtime.json' entry; found 0" `
        -Action { Invoke-PolicyValidation -Root $testRoot }

    Reset-TestArtifacts
    Assert-Fails `
        -MessagePattern 'approved immutable OpenClaw commit' `
        -Action {
            Invoke-PolicyValidation `
                -Root $testRoot `
                -RequestedRef 'v2026.8.2'
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Set-Content `
            -LiteralPath (
                Join-Path $Expanded 'session-host\x64\openclaw-session-host.exe'
            ) `
            -Value 'tampered' `
            -Encoding utf8
    }
    Assert-Fails `
        -MessagePattern 'session host file is invalid' `
        -Action { Invoke-PolicyValidation -Root $testRoot }

    Reset-TestArtifacts
    $x64MetadataPath = Join-Path $testRoot 'x64\msix-metadata.json'
    $x64Metadata = Get-Content -LiteralPath $x64MetadataPath -Raw |
        ConvertFrom-Json
    $x64Metadata.sourceTreeDirty = $true
    $x64Metadata |
        ConvertTo-Json |
        Set-Content -LiteralPath $x64MetadataPath -Encoding utf8
    Assert-Fails `
        -MessagePattern 'metadata is not eligible' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -PayloadPackageVersion '2026.9.3'
    Assert-Fails `
        -MessagePattern 'metadata is not eligible' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture arm64 `
        -PayloadCommit ('3' * 40)
    Assert-Fails `
        -MessagePattern 'metadata is not eligible' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -IncludeBundledNode $true
    Assert-Fails `
        -MessagePattern 'x64 MSIX has unexpected Node.js content' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -IncludeApplicationBundledNode $true
    Assert-Fails `
        -MessagePattern 'x64 MSIX has unexpected Node.js content' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -IncludeApplicationNodeArchive $true
    Assert-Fails `
        -MessagePattern 'x64 MSIX has unexpected Node.js content' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -IncludeEncodedScopedDependency $true
    Invoke-PolicyValidation -Root $testRoot

    Reset-TestArtifacts
    Copy-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -IncludeEncodedScopedDependency $true
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        $decodedDirectory = Join-Path `
            $Expanded `
            'app\node_modules\@scope'
        New-Item `
            -Path $decodedDirectory `
            -ItemType Directory `
            -Force |
            Out-Null
        Copy-Item `
            -LiteralPath (
                Join-Path `
                    $Expanded `
                    'app\node_modules\%40scope\package.json'
            ) `
            -Destination (Join-Path $decodedDirectory 'package.json')
    }
    Assert-Fails `
        -MessagePattern 'duplicate decoded path' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Set-Content `
            -LiteralPath (Join-Path $Expanded 'app\openclaw.mjs') `
            -Value 'tampered' `
            -Encoding utf8
    }
    Assert-Fails `
        -MessagePattern 'application file is invalid' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Remove-Item -LiteralPath (Join-Path $Expanded 'app\openclaw.mjs')
    }
    Assert-Fails `
        -MessagePattern "Expected one 'app/openclaw.mjs' entry; found 0" `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Set-Content `
            -LiteralPath (Join-Path $Expanded 'app\unexpected.js') `
            -Value 'unexpected' `
            -Encoding utf8
    }
    Assert-Fails `
        -MessagePattern 'application file set is invalid' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        $inventoryPath = Join-Path $Expanded 'payload\payload-files.json'
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw |
            ConvertFrom-Json
        $inventory.files = @($inventory.files) + @($inventory.files)
        $inventory |
            # Keep the nested inventory file records intact.
            ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath $inventoryPath -Encoding utf8
    }
    $x64MetadataPath = Join-Path $testRoot 'x64\msix-metadata.json'
    $x64Metadata = Get-Content -LiteralPath $x64MetadataPath -Raw |
        ConvertFrom-Json
    $x64Metadata.payloadFileCount = 2
    $x64Metadata |
        ConvertTo-Json |
        Set-Content -LiteralPath $x64MetadataPath -Encoding utf8
    Assert-Fails `
        -MessagePattern 'inventory has duplicate paths' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        $inventoryPath = Join-Path $Expanded 'payload\payload-files.json'
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw |
            ConvertFrom-Json
        $inventory.files[0].path = 'sub\..\openclaw.mjs'
        $inventory |
            # Keep the nested inventory file records intact.
            ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath $inventoryPath -Encoding utf8
    }
    Assert-Fails `
        -MessagePattern 'payload inventory is invalid' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Add-Content `
        -LiteralPath (Join-Path $testRoot 'x64\OpenClawGateway-x64.msix') `
        -Value 'tampered'
    Assert-Fails `
        -MessagePattern 'MSIX hash does not match' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    $substitutedX64 = Join-Path $testRoot 'substituted-x64.msix'
    Copy-Item `
        -LiteralPath (Join-Path `
            $testRoot `
            'x64\OpenClawGateway-x64.msix') `
        -Destination $substitutedX64
    Add-Content -LiteralPath $substitutedX64 -Value 'substituted'
    New-TestBundle -Root $testRoot -X64Package $substitutedX64
    Assert-Fails `
        -MessagePattern 'does not match the authorized standalone package' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot -PreserveBundle
        }

    foreach ($canonicalFile in $canonicalFileHashes.Keys) {
        $currentHash = (
            Get-FileHash -LiteralPath $canonicalFile -Algorithm SHA256
        ).Hash
        if ($currentHash -cne $canonicalFileHashes[$canonicalFile]) {
            throw "A test case mutated the canonical artifact '$canonicalFile'."
        }
    }

    Write-Host 'Gateway MSIX signing policy tests passed.'
}
finally {
    Remove-Item -LiteralPath $suiteRoot -Recurse -Force -ErrorAction SilentlyContinue
}
