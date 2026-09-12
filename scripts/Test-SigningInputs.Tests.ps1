[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$policyPath = Join-Path $repositoryRoot 'release-policy.json'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$approvedCommit = [string]$policy.approvedCommit
$packagingCommit = '1111111111111111111111111111111111111111'
$testRoot = Join-Path $env:TEMP (
    "openclaw-signing-policy-$([guid]::NewGuid().ToString('N'))"
)

# The validator checks staged MXC files against mxc-runtime.lock.json by hash.
# Real 9 MB signed binaries cannot be fabricated here, so the fixture builds
# small stand-ins and a matching lock, and the validator is pointed at it. The
# production lock stays the pinned public one.
$mxcFixtureRoot = Join-Path $env:TEMP (
    "openclaw-signing-mxc-$([guid]::NewGuid().ToString('N'))"
)
$mxcLockPath = Join-Path $mxcFixtureRoot 'mxc-runtime.lock.json'
$mxcPackage = '@microsoft/mxc-sdk'
$mxcVersion = '0.8.0-test'
$mxcIntegrity = 'sha512-' + [Convert]::ToBase64String(
    [byte[]](1..64 | ForEach-Object { [byte]$_ })
)

function New-TestMxcFixture {
    New-Item -Path $mxcFixtureRoot -ItemType Directory -Force | Out-Null
    $architectureLocks = [ordered]@{}
    foreach ($architecture in @('x64', 'arm64')) {
        $source = Join-Path $mxcFixtureRoot $architecture
        New-Item -Path $source -ItemType Directory -Force | Out-Null
        $files = @()
        foreach ($fileName in @('wxc-exec.exe', 'plm.exe')) {
            $path = Join-Path $source $fileName
            Set-Content `
                -LiteralPath $path `
                -Value "$fileName-$architecture" `
                -NoNewline
            $files += [ordered]@{
                archivePath = "package/bin/$architecture/$fileName"
                stagedPath = $fileName
                length = (Get-Item -LiteralPath $path).Length
                sha256 = (
                    Get-FileHash -LiteralPath $path -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            }
        }

        $licensePath = Join-Path $source 'LICENSE.md'
        Set-Content -LiteralPath $licensePath -Value 'MIT' -NoNewline
        $architectureLocks[$architecture] = [ordered]@{ files = $files }
    }

    $licenseSource = Join-Path $mxcFixtureRoot 'x64\LICENSE.md'
    [ordered]@{
        package = $mxcPackage
        version = $mxcVersion
        tarballIntegrity = $mxcIntegrity
        architectures = $architectureLocks
        licenseFiles = @(
            [ordered]@{
                archivePath = 'package/LICENSE.md'
                stagedPath = 'LICENSE.md'
                length = (Get-Item -LiteralPath $licenseSource).Length
                sha256 = (
                    Get-FileHash -LiteralPath $licenseSource -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            }
        )
    } |
        ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $mxcLockPath -Encoding utf8
}

function New-TestArtifact {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [ValidateSet('x64', 'arm64')]
        [string]$Architecture,

        [string]$PayloadCommit = $approvedCommit,

        [bool]$SourceTreeDirty = $false,

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
  <Identity Name="OpenClaw.Gateway"
            Publisher="$($policy.publisher)"
            Version="0.1.1.0"
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

    $mxcDirectory = Join-Path $staging "mxc\$Architecture"
    New-Item -Path $mxcDirectory -ItemType Directory -Force | Out-Null
    Copy-Item `
        -Path (Join-Path $mxcFixtureRoot "$Architecture\*") `
        -Destination $mxcDirectory
    [ordered]@{
        package = $mxcPackage
        version = $mxcVersion
        architecture = $Architecture
        tarballIntegrity = $mxcIntegrity
    } |
        ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $mxcDirectory 'mxc-runtime.json') `
            -Encoding utf8
    $mxcRuntimeFileCount = @(
        Get-ChildItem -LiteralPath $mxcDirectory -File
    ).Count

    $sessionHostDirectory = Join-Path $staging "session-host\$Architecture"
    New-Item -Path $sessionHostDirectory -ItemType Directory -Force | Out-Null
    $sessionHostFileName = 'openclaw-session-host.exe'
    $sessionHostPath = Join-Path $sessionHostDirectory $sessionHostFileName
    Set-Content `
        -LiteralPath $sessionHostPath `
        -Value "session-host-$Architecture" `
        -NoNewline
    $sessionHostSha256 = (
        Get-FileHash -LiteralPath $sessionHostPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()

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
        payloadLayout = 'immutable-package'
        payloadFileCount = $payloadFiles.Count
        mxcRuntimePackage = $mxcPackage
        mxcRuntimeVersion = $mxcVersion
        mxcRuntimeIntegrity = $mxcIntegrity
        mxcRuntimeFileCount = $mxcRuntimeFileCount
        sessionHostFileName = $sessionHostFileName
        sessionHostSha256 = $sessionHostSha256
        architecture = $Architecture
        archive = $msixName
        sha256 = $msixHash
        signed = $false
        packageVersion = '0.1.1.0'
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

        [string]$RequestedRef = $approvedCommit
    )

    & (Join-Path $PSScriptRoot 'Test-SigningInputs.ps1') `
        -ArtifactsDirectory $Root `
        -PolicyPath $policyPath `
        -RequestedRef $RequestedRef `
        -PackagingCommit $packagingCommit `
        -MxcRuntimeLockPath $mxcLockPath
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

function Reset-TestArtifacts {
    Remove-Item `
        -LiteralPath $testRoot `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    New-TestArtifact -Root $testRoot -Architecture x64
    New-TestArtifact -Root $testRoot -Architecture arm64
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-TestMxcFixture
    Reset-TestArtifacts
    Invoke-PolicyValidation -Root $testRoot

    Assert-Fails `
        -MessagePattern 'approved immutable OpenClaw commit' `
        -Action {
            Invoke-PolicyValidation `
                -Root $testRoot `
                -RequestedRef 'v2026.8.2'
        }

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

    Remove-Item -LiteralPath $testRoot -Recurse -Force
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    New-TestArtifact -Root $testRoot -Architecture x64
    New-TestArtifact `
        -Root $testRoot `
        -Architecture arm64 `
        -PayloadCommit ('3' * 40)
    Assert-Fails `
        -MessagePattern 'metadata is not eligible' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Remove-Item -LiteralPath $testRoot -Recurse -Force
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    New-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -IncludeBundledNode $true
    New-TestArtifact -Root $testRoot -Architecture arm64
    Assert-Fails `
        -MessagePattern 'x64 MSIX bundles Node.js' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Remove-Item -LiteralPath $testRoot -Recurse -Force
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    New-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -IncludeApplicationBundledNode $true
    New-TestArtifact -Root $testRoot -Architecture arm64
    Assert-Fails `
        -MessagePattern 'x64 MSIX bundles Node.js' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Remove-Item -LiteralPath $testRoot -Recurse -Force
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    New-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -IncludeApplicationNodeArchive $true
    New-TestArtifact -Root $testRoot -Architecture arm64
    Assert-Fails `
        -MessagePattern 'x64 MSIX bundles Node.js' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Remove-Item -LiteralPath $testRoot -Recurse -Force
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    New-TestArtifact `
        -Root $testRoot `
        -Architecture x64 `
        -IncludeEncodedScopedDependency $true
    New-TestArtifact -Root $testRoot -Architecture arm64
    Invoke-PolicyValidation -Root $testRoot

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
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Set-Content `
            -LiteralPath (Join-Path $Expanded 'mxc\x64\wxc-exec.exe') `
            -Value 'substituted-executor' `
            -NoNewline
    }
    Assert-Fails `
        -MessagePattern 'does not match its pinned hash' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Set-Content `
            -LiteralPath (Join-Path $Expanded 'mxc\x64\extra-tool.exe') `
            -Value 'unpinned' `
            -NoNewline
    }
    Assert-Fails `
        -MessagePattern 'MXC runtime file set does not match' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Remove-Item -LiteralPath (Join-Path $Expanded 'mxc\x64\plm.exe')
    }
    Assert-Fails `
        -MessagePattern 'MXC runtime file set does not match' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        $provenancePath = Join-Path $Expanded 'mxc\x64\mxc-runtime.json'
        $provenance = Get-Content -LiteralPath $provenancePath -Raw |
            ConvertFrom-Json
        $provenance.version = '0.9.0-unreviewed'
        $provenance |
            ConvertTo-Json |
            Set-Content -LiteralPath $provenancePath -Encoding utf8
    }
    Assert-Fails `
        -MessagePattern 'MXC runtime provenance is invalid' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    $x64MxcMetadataPath = Join-Path $testRoot 'x64\msix-metadata.json'
    $x64MxcMetadata = Get-Content -LiteralPath $x64MxcMetadataPath -Raw |
        ConvertFrom-Json
    $x64MxcMetadata.mxcRuntimeVersion = '0.9.0-unreviewed'
    $x64MxcMetadata |
        ConvertTo-Json |
        Set-Content -LiteralPath $x64MxcMetadataPath -Encoding utf8
    Assert-Fails `
        -MessagePattern 'not eligible for signing' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    # The guest helper runs inside the isolated session, so substitution,
    # removal, an extra reachable file, and metadata drift must each be caught.
    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Set-Content `
            -LiteralPath (
                Join-Path $Expanded 'session-host\x64\openclaw-session-host.exe'
            ) `
            -Value 'substituted-helper' `
            -NoNewline
    }
    Assert-Fails `
        -MessagePattern 'guest helper does not match' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Remove-Item -LiteralPath (
            Join-Path $Expanded 'session-host\x64\openclaw-session-host.exe'
        )
    }
    Assert-Fails `
        -MessagePattern 'must contain exactly the guest helper' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    Update-TestMsix -Root $testRoot -Architecture x64 -Mutator {
        param($Expanded)
        Set-Content `
            -LiteralPath (Join-Path $Expanded 'session-host\x64\extra.dll') `
            -Value 'unverified' `
            -NoNewline
    }
    Assert-Fails `
        -MessagePattern 'must contain exactly the guest helper' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Reset-TestArtifacts
    $x64HelperMetadataPath = Join-Path $testRoot 'x64\msix-metadata.json'
    $x64HelperMetadata = Get-Content -LiteralPath $x64HelperMetadataPath -Raw |
        ConvertFrom-Json
    $x64HelperMetadata.sessionHostFileName = 'something-else.exe'
    $x64HelperMetadata |
        ConvertTo-Json |
        Set-Content -LiteralPath $x64HelperMetadataPath -Encoding utf8
    Assert-Fails `
        -MessagePattern 'not eligible for signing' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Write-Host 'Gateway MSIX signing policy tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item `
        -LiteralPath $mxcFixtureRoot `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue
}
