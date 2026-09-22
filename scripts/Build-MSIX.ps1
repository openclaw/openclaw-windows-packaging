[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PayloadDirectory,

    [string]$NodeArchivePath,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [Parameter(Mandatory)]
    [string]$PackageVersion,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$SourceCommit,

    [switch]$SourceTreeDirty,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path `
    $repositoryRoot `
    'src\OpenClaw.Launcher\OpenClaw.Launcher.csproj'
$policy = Get-Content `
    -LiteralPath (Join-Path $repositoryRoot 'release-policy.json') `
    -Raw |
    ConvertFrom-Json
$manifestPath = Join-Path `
    $repositoryRoot `
    'src\OpenClaw.Launcher\Package.appxmanifest'
[xml]$packageManifest = Get-Content -LiteralPath $manifestPath -Raw
$manifestIdentity = $packageManifest.Package.Identity
if (
    [string]::IsNullOrWhiteSpace([string]$policy.packageFamilyName) -or
    [string]$manifestIdentity.Name -cne [string]$policy.packageIdentityName -or
    [string]$manifestIdentity.Publisher -cne [string]$policy.publisher
) {
    throw 'The package manifest identity does not match release policy.'
}
$packageIdentityName = [string]$policy.packageIdentityName
$packageFamilyName = [string]$policy.packageFamilyName
$publisher = [string]$policy.publisher

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

function Remove-DirectoryIfPresent {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([IO.Directory]::Exists($Path)) {
        [IO.Directory]::Delete($Path, $true)
    }
}

function Test-PackageVersion {
    $segments = @($PackageVersion.Split('.'))
    if ($segments.Count -ne 4) {
        throw 'PackageVersion must contain four numeric components.'
    }

    foreach ($segment in $segments) {
        [uint16]$value = 0
        if (-not [uint16]::TryParse($segment, [ref]$value)) {
            throw "Invalid MSIX package version component: $segment"
        }
        if ($value -gt 65534) {
            throw (
                "PackageVersion component $segment exceeds the .NET " +
                'assembly version maximum of 65534.'
            )
        }
    }
}

function Assert-ApplicationDoesNotBundleNode {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $bundledNodeEntries = @(
        Get-ChildItem -LiteralPath $Path -File -Force -Recurse |
            Where-Object {
                $_.Name -ieq 'node.exe' -or
                $_.Name -match '^node-v\d'
            }
    )
    if ($bundledNodeEntries.Count -ne 0) {
        throw (
            'The OpenClaw payload must not bundle Node.js: ' +
            (
                $bundledNodeEntries |
                    ForEach-Object {
                        [IO.Path]::GetRelativePath($Path, $_.FullName)
                    } |
                    Sort-Object
            ) -join ', '
        )
    }
}

function Assert-ApplicationHasNoReparsePoints {
    # npm installs can produce symlinks/junctions (e.g. workspace links).
    # Reject them: the per-file inventory hashes file content by path, and a
    # link could point outside the expanded tree or resolve differently than
    # what was hashed at build time.
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $reparsePoint = Get-ChildItem `
        -LiteralPath $Path `
        -Force `
        -Recurse |
        Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        } |
        Select-Object -First 1
    if ($null -ne $reparsePoint) {
        throw (
            'The OpenClaw payload must not contain links or reparse points: ' +
            [IO.Path]::GetRelativePath($Path, $reparsePoint.FullName)
        )
    }
}

function Assert-NodeArchive {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ExpectedRoot
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $nodeEntries = @(
            $archive.Entries |
                Where-Object {
                    $_.FullName -ieq "$ExpectedRoot/node.exe"
                }
        )
        if ($nodeEntries.Count -ne 1) {
            throw (
                "The Node.js archive must contain exactly one " +
                "'$ExpectedRoot/node.exe' entry."
            )
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Add-VswhereToPath {
    if (Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue) {
        return
    }

    $vswhereDirectory = Join-Path `
        ${env:ProgramFiles(x86)} `
        'Microsoft Visual Studio\Installer'
    $vswherePath = Join-Path $vswhereDirectory 'vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
        throw (
            'vswhere.exe was not found. Install Visual Studio Build Tools with ' +
            'the Desktop development with C++ workload.'
        )
    }

    $env:Path = "$vswhereDirectory;$env:Path"
}

Test-PackageVersion

$PayloadDirectory = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$payloadApplication = Join-Path $PayloadDirectory 'app'
$payloadMetadata = Join-Path $PayloadDirectory 'payload-metadata.json'
if (-not (Test-Path -LiteralPath $payloadApplication -PathType Container)) {
    throw "Required MSIX input was not found: $payloadApplication"
}
if (-not (Test-Path -LiteralPath $payloadMetadata -PathType Leaf)) {
    throw "Required MSIX input was not found: $payloadMetadata"
}

$payloadInfo = Get-Content -LiteralPath $payloadMetadata -Raw | ConvertFrom-Json
if (
    $payloadInfo.repository -ne 'https://github.com/openclaw/openclaw' -or
    $payloadInfo.architecture -ne $Architecture -or
    $payloadInfo.layout -ne 'expanded-directory' -or
    $payloadInfo.nodeVersion -isnot [string] -or
    $payloadInfo.nodeVersion -notmatch '^v?\d+\.\d+\.\d+$' -or
    [string]::IsNullOrWhiteSpace([string]$payloadInfo.packageVersion) -or
    $payloadInfo.requestedRef -isnot [string] -or
    [string]::IsNullOrWhiteSpace($payloadInfo.requestedRef) -or
    $payloadInfo.resolvedCommit -notmatch '^[0-9a-fA-F]{40}$'
) {
    throw 'Payload metadata is not valid for this MSIX package.'
}

$nodeVersion = $payloadInfo.nodeVersion.TrimStart('v')
$expectedNodeArchiveName = "node-v$nodeVersion-win-$Architecture.zip"
if ($NodeArchivePath) {
    $NodeArchivePath = (Resolve-Path -LiteralPath $NodeArchivePath).Path
    if ([IO.Path]::GetFileName($NodeArchivePath) -cne $expectedNodeArchiveName) {
        throw (
            'NodeArchivePath must match the payload build runtime: ' +
            $expectedNodeArchiveName
        )
    }
}

if (-not (Test-Path `
    -LiteralPath (Join-Path $payloadApplication 'openclaw.mjs') `
    -PathType Leaf)) {
    throw 'Expanded payload does not contain openclaw.mjs.'
}
Assert-ApplicationHasNoReparsePoints -Path $payloadApplication
Assert-ApplicationDoesNotBundleNode -Path $payloadApplication

$contentRoot = Join-Path $repositoryRoot 'content'
$openClawContent = Join-Path $contentRoot 'openclaw'
$applicationTarget = Join-Path $openClawContent 'app'
$runtimeTargetDirectory = Join-Path $openClawContent 'runtime'
$nodeArchiveTarget = Join-Path `
    $runtimeTargetDirectory `
    $expectedNodeArchiveName
New-Item -Path $openClawContent -ItemType Directory -Force | Out-Null

if (
    [IO.Path]::GetFullPath($payloadApplication) -ne
    [IO.Path]::GetFullPath($applicationTarget)
) {
    Remove-DirectoryIfPresent -Path $applicationTarget
    Copy-Item `
        -LiteralPath $payloadApplication `
        -Destination $applicationTarget `
        -Recurse
}

New-Item -Path $runtimeTargetDirectory -ItemType Directory -Force | Out-Null
if (-not $NodeArchivePath) {
    $archiveUri = "https://nodejs.org/dist/v$nodeVersion/$expectedNodeArchiveName"
    Write-Host "Downloading bundled Node.js runtime from $archiveUri."
    Invoke-WebRequest -Uri $archiveUri -OutFile $nodeArchiveTarget
}
elseif ($NodeArchivePath -ne $nodeArchiveTarget) {
    Copy-Item -LiteralPath $NodeArchivePath -Destination $nodeArchiveTarget -Force
}
Assert-NodeArchive `
    -Path $nodeArchiveTarget `
    -ExpectedRoot ([IO.Path]::GetFileNameWithoutExtension($expectedNodeArchiveName))
$nodeArchiveHash = (
    Get-FileHash -LiteralPath $nodeArchiveTarget -Algorithm SHA256
).Hash.ToLowerInvariant()

$payloadSymbols = @(
    # MSBuild's own AppxPackagePayload step strips .pdb files when it later
    # packages content\openclaw\app; remove them here too so the inventory
    # built below matches what actually ends up in the MSIX.
    Get-ChildItem `
        -LiteralPath $applicationTarget `
        -Filter '*.pdb' `
        -File `
        -Force `
        -Recurse
)
foreach ($payloadSymbol in $payloadSymbols) {
    Remove-Item -LiteralPath $payloadSymbol.FullName -Force
}
if ($payloadSymbols.Count -ne 0) {
    Write-Host (
        "Excluded $($payloadSymbols.Count) payload PDB files from the MSIX."
    )
}

$payloadFiles = @(
    Get-ChildItem -LiteralPath $applicationTarget -File -Force -Recurse |
        ForEach-Object {
            [ordered]@{
                path = (
                    [IO.Path]::GetRelativePath(
                        $applicationTarget,
                        $_.FullName
                    )
                ).Replace('\', '/')
                length = $_.Length
                sha256 = (
                    Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            }
        } |
        Sort-Object path
)
if ($payloadFiles.Count -eq 0) {
    throw 'Expanded payload contains no files.'
}
$payloadInventoryPath = Join-Path $openClawContent 'payload-files.json'
[ordered]@{
    files = $payloadFiles
} |
    # Keep the nested inventory file records intact.
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $payloadInventoryPath -Encoding utf8

& (Join-Path $PSScriptRoot 'Get-MxcRuntime.ps1') `
    -Architecture $Architecture

$mxcRuntimeDirectory = Join-Path $repositoryRoot "content\mxc\$Architecture"
$mxcRuntimeFiles = @(
    Get-ChildItem -LiteralPath $mxcRuntimeDirectory -File -Force -Recurse |
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
                    Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            }
        } |
        Sort-Object path
)
if (
    $mxcRuntimeFiles.Count -eq 0 -or
    -not ($mxcRuntimeFiles.path -contains 'wxc-exec.exe') -or
    -not ($mxcRuntimeFiles.path -contains 'mxc-runtime.json')
) {
    throw "The staged $Architecture MXC runtime is incomplete."
}
$mxcProvenance = Get-Content `
    -LiteralPath (Join-Path $mxcRuntimeDirectory 'mxc-runtime.json') `
    -Raw |
    ConvertFrom-Json

$temporaryRoot = if ($env:RUNNER_TEMP) {
    $env:RUNNER_TEMP
}
else {
    [IO.Path]::GetTempPath()
}
$workRoot = Join-Path `
    $temporaryRoot `
    "openclaw-msix-$Architecture-$([guid]::NewGuid().ToString('N'))"
$msixBuildDirectory = Join-Path $workRoot 'appx'
New-Item `
    -Path $msixBuildDirectory, $OutputDirectory `
    -ItemType Directory `
    -Force |
    Out-Null

try {
    Add-VswhereToPath
    $sessionHostProject = Join-Path `
        $repositoryRoot `
        'src\OpenClaw.SessionHost\OpenClaw.SessionHost.csproj'
    $sessionHostOutput = Join-Path `
        $repositoryRoot `
        "content\session-host\$Architecture"
    Remove-DirectoryIfPresent -Path $sessionHostOutput
    New-Item -Path $sessionHostOutput -ItemType Directory -Force | Out-Null
    Write-Host "Publishing the NativeAOT win-$Architecture session host."
    Invoke-CheckedCommand `
        -FailureMessage 'NativeAOT session host publish failed.' `
        -Command {
            & dotnet publish $sessionHostProject `
                --configuration Release `
                --runtime "win-$Architecture" `
                --self-contained `
                --no-restore `
                "-p:Platform=$Architecture" `
                -p:PublishAot=true `
                --output $sessionHostOutput `
                --nologo
        }
    $sessionHostFiles = @(
        Get-ChildItem -LiteralPath $sessionHostOutput -File -Force -Recurse |
            Where-Object Extension -notin '.pdb', '.xml' |
            ForEach-Object {
                [ordered]@{
                    path = (
                        [IO.Path]::GetRelativePath(
                            $sessionHostOutput,
                            $_.FullName
                        )
                    ).Replace('\', '/')
                    length = $_.Length
                    sha256 = (
                        Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                    ).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object path
    )
    if (-not ($sessionHostFiles.path -contains 'openclaw-session-host.exe')) {
        throw "The published $Architecture session host is incomplete."
    }

    $appxOutput = $msixBuildDirectory.TrimEnd('\') + '\'
    Write-Host "Building unsigned NativeAOT win-$Architecture MSIX with MSBuild."
    Invoke-CheckedCommand `
        -FailureMessage 'NativeAOT MSIX build failed.' `
        -Command {
            & dotnet build $projectPath `
                --configuration Release `
                --runtime "win-$Architecture" `
                --no-restore `
                "-p:Platform=$Architecture" `
                "-p:RuntimeIdentifiers=win-$Architecture" `
                -p:PublishAot=true `
                -p:SelfContained=true `
                -p:IncludePackagingContent=true `
                "-p:NodeRuntimeArchiveFileName=$expectedNodeArchiveName" `
                -p:GenerateAppxPackageOnBuild=true `
                "-p:AssemblyVersion=$PackageVersion" `
                "-p:FileVersion=$PackageVersion" `
                "-p:PackageIdentityVersion=$PackageVersion" `
                "-p:ClawCtlPackageVersion=$PackageVersion" `
                "-p:ClawCtlPackageCommit=$($SourceCommit.ToLowerInvariant())" `
                "-p:ClawCtlPayloadVersion=$([string]$payloadInfo.packageVersion)" `
                "-p:ClawCtlPayloadCommit=$($payloadInfo.resolvedCommit.ToLowerInvariant())" `
                "-p:AppxPackageDir=$appxOutput" `
                -p:AppxBundle=Never `
                -p:AppxPackageSigningEnabled=false `
                -p:DebugType=None `
                --nologo
        }

    $builtPackages = @(
        Get-ChildItem `
            -LiteralPath $msixBuildDirectory `
            -Filter '*.msix' `
            -File `
            -Recurse
    )
    if ($builtPackages.Count -ne 1) {
        throw (
            "Expected one MSIX under '$msixBuildDirectory'; " +
            "found $($builtPackages.Count)."
        )
    }

    $msixName = "OpenClawGateway-$Architecture.msix"
    $msixPath = Join-Path $OutputDirectory $msixName
    Copy-Item -LiteralPath $builtPackages[0].FullName -Destination $msixPath -Force

    $expectedPackageFiles =
        [System.Collections.Generic.Dictionary[string, object]]::new(
            [System.StringComparer]::OrdinalIgnoreCase
        )
    foreach ($applicationFile in $payloadFiles) {
        $expectedPackageFiles.Add(
            "app/$($applicationFile.path)",
            [pscustomobject]@{
                Hash = $applicationFile.sha256
            }
        )
    }
    $expectedPackageFiles.Add(
        'payload/payload-files.json',
        [pscustomobject]@{
            Hash = (
                Get-FileHash `
                    -LiteralPath $payloadInventoryPath `
                    -Algorithm SHA256
            ).Hash.ToLowerInvariant()
        }
    )
    $expectedPackageFiles.Add(
        "runtime/$expectedNodeArchiveName",
        [pscustomobject]@{
            Hash = $nodeArchiveHash
        }
    )
    foreach ($mxcFile in $mxcRuntimeFiles) {
        $expectedPackageFiles.Add(
            "mxc/$Architecture/$($mxcFile.path)",
            [pscustomobject]@{
                Hash = $mxcFile.sha256
            }
        )
    }
    foreach ($sessionHostFile in $sessionHostFiles) {
        $expectedPackageFiles.Add(
            "session-host/$Architecture/$($sessionHostFile.path)",
            [pscustomobject]@{
                Hash = $sessionHostFile.sha256
            }
        )
    }
    $packageEntries = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageArchive = [System.IO.Compression.ZipFile]::OpenRead($msixPath)
    try {
        foreach ($entry in $packageArchive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }

            $decodedPath = [Uri]::UnescapeDataString($entry.FullName)
            if (-not $packageEntries.Add($decodedPath)) {
                throw "The MSIX contains a duplicate decoded path: $decodedPath"
            }
            $expectedEntry = $null
            if (
                -not $expectedPackageFiles.Remove(
                    $decodedPath,
                    [ref]$expectedEntry
                )
            ) {
                continue
            }

            $stream = $entry.Open()
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try {
                $packagedHash = [Convert]::ToHexString(
                    $sha256.ComputeHash($stream)
                ).ToLowerInvariant()
            }
            finally {
                $sha256.Dispose()
                $stream.Dispose()
            }

            if ($packagedHash -ne $expectedEntry.Hash) {
                throw "MSBuild changed package content: $decodedPath"
            }

        }

        $manifestEntry = $packageArchive.GetEntry('AppxManifest.xml')
        if (-not $manifestEntry) {
            throw 'The MSIX does not contain AppxManifest.xml.'
        }

        $manifestStream = $manifestEntry.Open()
        $manifestReader = [IO.StreamReader]::new($manifestStream)
        try {
            [xml]$manifest = $manifestReader.ReadToEnd()
        }
        finally {
            $manifestReader.Dispose()
            $manifestStream.Dispose()
        }

        $aliasExtension = @(
            $manifest.SelectNodes(
                "//*[local-name()='Extension' and @Category='windows.appExecutionAlias']"
            )
        )
        if ($aliasExtension.Count -ne 2) {
            throw 'The MSIX must contain public and control app execution alias extensions.'
        }
        foreach ($extension in $aliasExtension) {
            if ($extension.Executable -ne 'openclaw.exe') {
                throw 'Both command aliases must target openclaw.exe.'
            }
        }

        $registeredAliases = @(
            $aliasExtension |
                ForEach-Object { $_.SelectNodes(".//*[local-name()='ExecutionAlias']") } |
                ForEach-Object { $_.Alias }
        )
        foreach ($requiredAlias in @('openclaw.exe', 'clawctl.exe')) {
            if ($requiredAlias -notin $registeredAliases) {
                throw "The MSIX does not register $requiredAlias."
            }
        }
    }
    finally {
        $packageArchive.Dispose()
    }

    if (-not $packageEntries.Contains('openclaw.exe')) {
        throw 'The MSIX does not contain the NativeAOT host executable.'
    }
    $unexpectedNodeEntries = @(
        $packageEntries |
            Where-Object {
                (
                    [IO.Path]::GetFileName($_) -ieq 'node.exe' -or
                    [IO.Path]::GetFileName($_) -match '^node-v\d'
                ) -and
                $_ -ine "runtime/$expectedNodeArchiveName"
            }
    )
    if ($unexpectedNodeEntries.Count -ne 0) {
        throw (
            'The MSIX contains unexpected Node.js content: ' +
            (($unexpectedNodeEntries | Sort-Object) -join ', ')
        )
    }
    foreach ($managedHostArtifact in @(
        'openclaw.dll',
        'openclaw.deps.json',
        'openclaw.runtimeconfig.json'
    )) {
        if ($packageEntries.Contains($managedHostArtifact)) {
            throw "The MSIX contains managed host artifact: $managedHostArtifact"
        }
    }

    if ($expectedPackageFiles.Count -ne 0) {
        throw (
            'MSBuild omitted package content: ' +
            (
                $expectedPackageFiles.Keys |
                    Sort-Object |
                    Select-Object -First 5
            ) -join ', '
        )
    }

    $msixHash = (
        Get-FileHash -LiteralPath $msixPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    [ordered]@{
        packagingRepository = 'https://github.com/openclaw/openclaw-windows-packaging'
        packagingCommit = $SourceCommit.ToLowerInvariant()
        sourceTreeDirty = $SourceTreeDirty.IsPresent
        payloadRepository = $payloadInfo.repository
        payloadRequestedRef = $payloadInfo.requestedRef
        payloadResolvedCommit = $payloadInfo.resolvedCommit.ToLowerInvariant()
        payloadPackageVersion = [string]$payloadInfo.packageVersion
        payloadLayout = 'immutable-package'
        payloadFileCount = $payloadFiles.Count
        nodeRuntimeVersion = $nodeVersion
        nodeRuntimeArchive = $expectedNodeArchiveName
        nodeRuntimeSha256 = $nodeArchiveHash
        mxcRuntimeVersion = [string]$mxcProvenance.version
        mxcRuntimeFiles = $mxcRuntimeFiles
        sessionHostFiles = $sessionHostFiles
        architecture = $Architecture
        archive = $msixName
        sha256 = $msixHash
        signed = $false
        packageVersion = $PackageVersion
        packageIdentityName = $packageIdentityName
        packageFamilyName = $packageFamilyName
        publisher = $publisher
    } | ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $OutputDirectory 'msix-metadata.json') `
            -Encoding utf8

    Write-Host "Created unsigned MSIX: $msixPath"
}
finally {
    Remove-DirectoryIfPresent -Path $workRoot
}
