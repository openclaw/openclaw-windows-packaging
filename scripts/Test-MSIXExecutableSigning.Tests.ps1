[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Fixture payload natives are deliberately unsigned; their warnings are noise.
$WarningPreference = 'SilentlyContinue'

if (-not $IsWindows) {
    throw 'MSIX executable signing tests require Windows.'
}

Import-Module (Join-Path $PSScriptRoot 'MSIXExecutableSigning.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$signingScript = Join-Path $PSScriptRoot 'Invoke-MSIXExecutableSigning.ps1'
$gateScript = Join-Path $PSScriptRoot 'Test-MSIXExecutableSignatures.ps1'
$bundleScript = Join-Path $PSScriptRoot 'Build-MSIXBundle.ps1'
$makeAppx = Resolve-WindowsSdkTool -Name MakeAppx.exe
$signTool = Resolve-WindowsSdkTool -Name signtool.exe
$publisher = 'CN=OpenClaw Fixture Signer'
$version = '1.2.3.0'
$architectures = @('x64', 'arm64')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw-msix-executable-signing-$([guid]::NewGuid().ToString('N'))"
)

# The gate requires node.exe to carry a trusted embedded signature. The
# running PowerShell host is such a file on every supported test machine.
$trustedExecutable = (Get-Process -Id $PID).Path
$trustedSignature = Get-AuthenticodeSignature -LiteralPath $trustedExecutable
if ($trustedSignature.Status -ne 'Valid' -or $trustedSignature.SignatureType -ne 'Authenticode') {
    throw "These tests need an Authenticode-signed PowerShell host; $trustedExecutable is $($trustedSignature.Status)."
}

function New-FixturePortableExecutable {
    param([string]$Path, [byte]$Marker)

    # A minimal PE32+ image with one section: enough structure for SignTool.
    $bytes = [byte[]]::new(0x400)
    $write = {
        param([int]$Offset, $Value)
        $data = [BitConverter]::GetBytes($Value)
        [Array]::Copy($data, 0, $bytes, $Offset, $data.Length)
    }
    $bytes[0] = 0x4D
    $bytes[1] = 0x5A
    & $write 0x3C ([int32]0x40)
    [Array]::Copy([byte[]](0x50, 0x45, 0, 0), 0, $bytes, 0x40, 4)
    $coff = 0x44
    & $write $coff ([uint16]0x8664)
    & $write ($coff + 2) ([uint16]1)
    & $write ($coff + 16) ([uint16]0xF0)
    & $write ($coff + 18) ([uint16]0x22)
    $optional = $coff + 20
    & $write $optional ([uint16]0x20B)
    & $write ($optional + 4) ([uint32]0x200)
    & $write ($optional + 16) ([uint32]0x1000)
    & $write ($optional + 20) ([uint32]0x1000)
    & $write ($optional + 24) ([uint64]0x140000000)
    & $write ($optional + 32) ([uint32]0x1000)
    & $write ($optional + 36) ([uint32]0x200)
    & $write ($optional + 40) ([uint16]6)
    & $write ($optional + 48) ([uint16]6)
    & $write ($optional + 56) ([uint32]0x2000)
    & $write ($optional + 60) ([uint32]0x200)
    & $write ($optional + 68) ([uint16]3)
    & $write ($optional + 70) ([uint16]0x8160)
    & $write ($optional + 72) ([uint64]0x100000)
    & $write ($optional + 80) ([uint64]0x1000)
    & $write ($optional + 88) ([uint64]0x100000)
    & $write ($optional + 96) ([uint64]0x1000)
    & $write ($optional + 108) ([uint32]16)
    $section = $optional + 0xF0
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('.text'), 0, $bytes, $section, 5)
    & $write ($section + 8) ([uint32]0x200)
    & $write ($section + 12) ([uint32]0x1000)
    & $write ($section + 16) ([uint32]0x200)
    & $write ($section + 20) ([uint32]0x200)
    & $write ($section + 36) ([uint32]0x60000020)
    $bytes[0x200] = 0xC3
    $bytes[0x201] = $Marker
    New-Item -Path (Split-Path $Path -Parent) -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function New-FixtureCertificate {
    param([string]$Subject, [string]$Path)

    $key = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $Subject,
            $key,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1
        )
        $usages = [Security.Cryptography.OidCollection]::new()
        [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $false)
        )
        $certificate = $request.CreateSelfSigned(
            [DateTimeOffset]::UtcNow.AddMinutes(-5),
            [DateTimeOffset]::UtcNow.AddDays(1)
        )
        $password = [guid]::NewGuid().ToString('N')
        # A PFX file keeps the fixture out of every certificate store.
        [IO.File]::WriteAllBytes($Path, $certificate.Export('Pfx', $password))
        [pscustomobject]@{ Path = $Path; Password = $password; Thumbprint = $certificate.Thumbprint }
    }
    finally {
        $key.Dispose()
    }
}

function Invoke-SignFiles {
    param([object]$Certificate, [string[]]$Files)

    $output = & $signTool sign /fd SHA256 /f $Certificate.Path /p $Certificate.Password @Files 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "Fixture signing failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256 {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-EntryHashes {
    param([string]$Path)

    $hashes = @{}
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) { continue }
            $stream = $entry.Open()
            try {
                $hashes[[Uri]::UnescapeDataString($entry.FullName)] = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData($stream)
                ).ToLowerInvariant()
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    $hashes
}

function New-FixtureArtifacts {
    param([string]$Root, [byte]$MarkerBase = 1, [switch]$Bundle)

    foreach ($architecture in $architectures) {
        $layout = Join-Path $Root "layouts\$architecture"
        New-Item -Path (Join-Path $layout 'Images') -ItemType Directory -Force | Out-Null
        [IO.File]::WriteAllBytes(
            (Join-Path $layout 'Images\StoreLogo.png'),
            [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=')
        )
        @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap rescap">
  <Identity Name="OpenClaw.Fixture" Publisher="$publisher" Version="$version" ProcessorArchitecture="$architecture" />
  <Properties>
    <DisplayName>OpenClaw Fixture</DisplayName>
    <PublisherDisplayName>OpenClaw</PublisherDisplayName>
    <Logo>Images\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.19041.0" />
  </Dependencies>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Applications>
    <Application Id="App" Executable="openclaw.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="OpenClaw Fixture" Description="Fixture" BackgroundColor="transparent" Square150x150Logo="Images\StoreLogo.png" Square44x44Logo="Images\StoreLogo.png" AppListEntry="none" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@ | Set-Content -LiteralPath (Join-Path $layout 'AppxManifest.xml') -Encoding utf8

        $marker = [byte]($MarkerBase * 10)
        New-FixturePortableExecutable -Path (Join-Path $layout 'openclaw.exe') -Marker $marker
        New-FixturePortableExecutable -Path (Join-Path $layout 'openclaw-gateway-recovery.exe') -Marker ($marker + 1)
        New-FixturePortableExecutable -Path (Join-Path $layout "session-host\$architecture\openclaw-session-host.exe") -Marker ($marker + 2)
        # Upstream payload natives are reported, not re-signed; names with
        # spaces and '@' exercise MakeAppx's percent-encoded entry names.
        New-FixturePortableExecutable -Path (Join-Path $layout 'app\node_modules\@scope\native pkg\addon.node') -Marker ($marker + 3)
        Set-Content -LiteralPath (Join-Path $layout 'app\node_modules\@scope\native pkg\read me+1.txt') -Value 'payload' -Encoding utf8

        $nodeRoot = Join-Path $Root "node\$architecture\node-v0.0.1-win-$architecture"
        New-Item -Path $nodeRoot -ItemType Directory -Force | Out-Null
        Copy-Item -LiteralPath $trustedExecutable -Destination (Join-Path $nodeRoot 'node.exe')
        New-Item -Path (Join-Path $layout 'runtime') -ItemType Directory -Force | Out-Null
        [IO.Compression.ZipFile]::CreateFromDirectory(
            (Split-Path $nodeRoot -Parent),
            (Join-Path $layout "runtime\node-v0.0.1-win-$architecture.zip")
        )

        $artifactDirectory = Join-Path $Root "artifacts\$architecture"
        New-Item -Path $artifactDirectory -ItemType Directory -Force | Out-Null
        $msix = Join-Path $artifactDirectory "OpenClawGateway-$architecture.msix"
        $output = & $makeAppx pack /d $layout /p $msix 2>&1
        if ($LASTEXITCODE -ne 0) {
            $output | ForEach-Object { Write-Host $_ }
            throw "Fixture MSIX packing failed with exit code $LASTEXITCODE."
        }

        $sessionHost = Join-Path $layout "session-host\$architecture\openclaw-session-host.exe"
        $recovery = Join-Path $layout 'openclaw-gateway-recovery.exe'
        [ordered]@{
            architecture = $architecture
            archive = "OpenClawGateway-$architecture.msix"
            sha256 = Get-Sha256 $msix
            signed = $false
            packageVersion = $version
            sessionHostFiles = @([ordered]@{
                    path = 'openclaw-session-host.exe'
                    length = (Get-Item $sessionHost).Length
                    sha256 = Get-Sha256 $sessionHost
                })
            gatewayRecoveryFiles = @([ordered]@{
                    path = 'openclaw-gateway-recovery.exe'
                    length = (Get-Item $recovery).Length
                    sha256 = Get-Sha256 $recovery
                })
        } | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $artifactDirectory 'msix-metadata.json') -Encoding utf8
    }

    if ($Bundle) {
        & $bundleScript `
            -X64Package (Join-Path $Root 'artifacts\x64\OpenClawGateway-x64.msix') `
            -Arm64Package (Join-Path $Root 'artifacts\arm64\OpenClawGateway-arm64.msix') `
            -PackageVersion $version `
            -OutputPath (Join-Path $Root 'artifacts\bundle\OpenClawGateway.msixbundle') `
            -MakeAppxPath $makeAppx | Out-Null
    }

    Join-Path $Root 'artifacts'
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Because)

    try {
        & $Action | Out-Null
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "$Because Unexpected failure: $($_.Exception.Message)"
        }
        return
    }
    throw $Because
}

function Get-CaseDirectories {
    param([string]$Name)

    $root = Join-Path $testRoot $Name
    [pscustomobject]@{
        Root = $root
        Work = Join-Path $root 'work'
        Signing = Join-Path $root 'signing'
        Output = Join-Path $root 'signed'
    }
}

try {
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    $certificate = New-FixtureCertificate -Subject $publisher -Path (Join-Path $testRoot 'signer.pfx')
    $otherCertificate = New-FixtureCertificate -Subject 'CN=Someone Else' -Path (Join-Path $testRoot 'other.pfx')

    # Signing the repository's executables yields the authorized packages plus
    # embedded signatures, a matching rebuilt bundle, and refreshed metadata.
    $case = Get-CaseDirectories 'sign'
    $artifacts = New-FixtureArtifacts -Root $case.Root -Bundle
    & $signingScript -Phase All `
        -ArtifactsDirectory $artifacts `
        -WorkDirectory $case.Work `
        -SigningDirectory $case.Signing `
        -OutputDirectory $case.Output `
        -PfxPath $certificate.Path `
        -PfxPassword $certificate.Password `
        -MakeAppxPath $makeAppx `
        -SignToolPath $signTool | Out-Null

    $catalog = [IO.File]::ReadAllLines((Join-Path $case.Signing 'files.txt'))
    if ($catalog.Count -ne 6 -or @($catalog | Where-Object { $_ -match 'addon\.node|node\.exe' }).Count -ne 0) {
        throw "The signing catalog must name exactly the repository's executables: $($catalog -join ', ')"
    }
    foreach ($architecture in $architectures) {
        $source = Join-Path $artifacts "$architecture\OpenClawGateway-$architecture.msix"
        $signed = Join-Path $case.Output "$architecture\OpenClawGateway-$architecture.msix"
        $sourceEntries = Get-EntryHashes $source
        $signedEntries = Get-EntryHashes $signed
        $owned = @('openclaw.exe', 'openclaw-gateway-recovery.exe', "session-host/$architecture/openclaw-session-host.exe")
        foreach ($entry in $sourceEntries.Keys) {
            $changed = $signedEntries[$entry] -cne $sourceEntries[$entry]
            if ($entry -in $owned) {
                if (-not $changed) { throw "$architecture $entry was not signed." }
            }
            elseif ($changed -and $entry -notin @('AppxBlockMap.xml', '[Content_Types].xml')) {
                throw "$architecture $entry changed while signing executables."
            }
        }
        if ($signedEntries.Count -ne $sourceEntries.Count -or $signedEntries.ContainsKey('AppxSignature.p7x')) {
            throw "The $architecture package gained or lost entries, or was package-signed."
        }

        $metadata = Get-Content -LiteralPath (Join-Path $case.Output "$architecture\msix-metadata.json") -Raw |
            ConvertFrom-Json
        if (
            $metadata.sha256 -cne (Get-Sha256 $signed) -or
            $metadata.signed -ne $false -or
            @($metadata.signedExecutables).Count -ne 3 -or
            $metadata.sessionHostFiles[0].sha256 -cne $signedEntries["session-host/$architecture/openclaw-session-host.exe"] -or
            $metadata.gatewayRecoveryFiles[0].sha256 -cne $signedEntries['openclaw-gateway-recovery.exe'] -or
            @($metadata.signedExecutables | Where-Object { $_.signerThumbprint -cne $certificate.Thumbprint }).Count -ne 0
        ) {
            throw "The $architecture metadata does not describe the signed package."
        }
    }
    $bundleEntries = Get-EntryHashes (Join-Path $case.Output 'bundle\OpenClawGateway.msixbundle')
    foreach ($architecture in $architectures) {
        if ($bundleEntries["OpenClawGateway-$architecture.msix"] -cne (Get-Sha256 (Join-Path $case.Output "$architecture\OpenClawGateway-$architecture.msix"))) {
            throw "The rebuilt bundle does not embed the signed $architecture package."
        }
    }
    & $gateScript -Path (Join-Path $case.Output 'bundle\OpenClawGateway.msixbundle') -ExpectedThumbprint $certificate.Thumbprint |
        Out-Null
    Write-Host 'Signing embeds signatures only in the repository executables and rebuilds a matching bundle.'

    # The gate is the release check: an unsigned helper, or one signed by
    # another identity, fails it.
    Assert-Throws `
        -Action { & $gateScript -Path (Join-Path $artifacts 'x64\OpenClawGateway-x64.msix') -ExpectedThumbprint $certificate.Thumbprint } `
        -Pattern 'openclaw-session-host\.exe is not Authenticode-signed' `
        -Because 'The gate accepted an unsigned session host.'
    Assert-Throws `
        -Action { & $gateScript -Path (Join-Path $case.Output 'x64\OpenClawGateway-x64.msix') -ExpectedThumbprint $otherCertificate.Thumbprint } `
        -Pattern 'thumbprint was unexpected' `
        -Because 'The gate accepted an executable signed by another identity.'
    Assert-Throws `
        -Action { & $gateScript -Path (Join-Path $case.Output 'x64\OpenClawGateway-x64.msix') -ExpectedPublisher $publisher } `
        -Pattern 'signature status was UnknownError' `
        -Because 'Publisher mode accepted a signature that does not chain to a trusted root.'
    Write-Host 'The gate rejects unsigned, foreign, and untrusted executable signatures.'

    # A signer may add a signature and nothing else; a different binary that
    # carries a valid signature from the expected identity is rejected.
    $case = Get-CaseDirectories 'substituted'
    $artifacts = New-FixtureArtifacts -Root $case.Root
    & $signingScript -Phase Prepare -ArtifactsDirectory $artifacts -WorkDirectory $case.Work -SigningDirectory $case.Signing -MakeAppxPath $makeAppx |
        Out-Null
    $substituted = Join-Path $case.Signing 'x64\session-host\x64\openclaw-session-host.exe'
    New-FixturePortableExecutable -Path $substituted -Marker 0xEE
    $signingFiles = @(Get-ChildItem -LiteralPath $case.Signing -Recurse -Filter '*.exe' | ForEach-Object FullName)
    Invoke-SignFiles -Certificate $certificate -Files $signingFiles
    Assert-Throws `
        -Action {
            & $signingScript -Phase Complete -ArtifactsDirectory $artifacts -WorkDirectory $case.Work `
                -SigningDirectory $case.Signing -OutputDirectory $case.Output `
                -ExpectedThumbprint $certificate.Thumbprint -MakeAppxPath $makeAppx
        } `
        -Pattern 'changed outside its Authenticode signature' `
        -Because 'Completion accepted a substituted executable.'
    if (Test-Path -LiteralPath $case.Output) {
        throw 'Completion produced output after rejecting a substituted executable.'
    }
    Write-Host 'Completion rejects executables changed beyond their signature.'

    # Executables must be signed before the package: a package-signed input
    # would carry a catalog that no longer matches its contents.
    $case = Get-CaseDirectories 'package-signed'
    $artifacts = New-FixtureArtifacts -Root $case.Root
    $packageSigned = Join-Path $artifacts 'x64\OpenClawGateway-x64.msix'
    Invoke-SignFiles -Certificate $certificate -Files @($packageSigned)
    $metadataPath = Join-Path $artifacts 'x64\msix-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $metadata.sha256 = Get-Sha256 $packageSigned
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    Assert-Throws `
        -Action {
            & $signingScript -Phase Prepare -ArtifactsDirectory $artifacts -WorkDirectory $case.Work `
                -SigningDirectory $case.Signing -MakeAppxPath $makeAppx
        } `
        -Pattern 'already package-signed' `
        -Because 'Preparation accepted a package-signed MSIX.'
    Write-Host 'Preparation rejects packages that are already package-signed.'

    # A bundle that embeds anything other than the authorized packages cannot
    # be rebuilt into a release bundle.
    $case = Get-CaseDirectories 'bundle-substituted'
    $artifacts = New-FixtureArtifacts -Root $case.Root -Bundle
    $other = New-FixtureArtifacts -Root (Join-Path $case.Root 'other') -MarkerBase 2 -Bundle
    Copy-Item -LiteralPath (Join-Path $other 'bundle\OpenClawGateway.msixbundle') -Destination (Join-Path $artifacts 'bundle\OpenClawGateway.msixbundle') -Force
    Assert-Throws `
        -Action {
            & $signingScript -Phase Prepare -ArtifactsDirectory $artifacts -WorkDirectory $case.Work `
                -SigningDirectory $case.Signing -MakeAppxPath $makeAppx
        } `
        -Pattern 'does not contain the authorized x64 package' `
        -Because 'Preparation accepted a bundle with substituted packages.'
    Write-Host 'Preparation rejects bundles that embed unauthorized packages.'

    # Test mode rehearses the release order with its temporary certificate:
    # executables first, then the packages and the bundle.
    $case = Get-CaseDirectories 'test-signing'
    $artifacts = New-FixtureArtifacts -Root $case.Root -Bundle
    $policyPath = Join-Path $case.Root 'release-policy.json'
    @{ publisher = $publisher; sideloadPackageIdentity = @{ publisher = $publisher } } |
        ConvertTo-Json | Set-Content -LiteralPath $policyPath -Encoding utf8
    $testKey = [Security.Cryptography.RSA]::Create(2048)
    $testRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $publisher,
        $testKey,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1
    )
    $testUsages = [Security.Cryptography.OidCollection]::new()
    [void]$testUsages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $testRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($testUsages, $false)
    )
    $testCertificate = $testRequest.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddMinutes(-5),
        [DateTimeOffset]::UtcNow.AddDays(1)
    )
    & (Join-Path $PSScriptRoot 'Sign-TestMSIX.ps1') `
        -ArtifactsDirectory $artifacts `
        -OutputDirectory $case.Output `
        -PolicyPath $policyPath `
        -SignExecutables `
        -Operations @{
            NewCertificate = { param($subject) $testCertificate }
            RemoveCertificate = { param($certificate) }
        } | Out-Null
    foreach ($signedPackage in @(
            (Join-Path $case.Output 'x64\OpenClawGateway-x64.msix'),
            (Join-Path $case.Output 'arm64\OpenClawGateway-arm64.msix'),
            (Join-Path $case.Output 'bundle\OpenClawGateway.msixbundle')
        )) {
        if ((Get-AuthenticodeSignature -LiteralPath $signedPackage).SignerCertificate.Thumbprint -cne $testCertificate.Thumbprint) {
            throw "Test signing did not sign $signedPackage."
        }
    }
    & $gateScript -Path (Join-Path $case.Output 'bundle\OpenClawGateway.msixbundle') -ExpectedThumbprint $testCertificate.Thumbprint |
        Out-Null
    $metadata = Get-Content -LiteralPath (Join-Path $case.Output 'x64\msix-metadata.json') -Raw | ConvertFrom-Json
    if ($metadata.signed -ne $true -or @($metadata.signedExecutables).Count -ne 3) {
        throw 'Test signing metadata does not record both signatures.'
    }
    Write-Host 'Test signing signs executables before packages and bundles.'

    Write-Host 'MSIX executable signing tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
