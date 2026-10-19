[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-sdk-integrity-$([guid]::NewGuid().ToString('N'))"
New-Item -Path $testRoot -ItemType Directory | Out-Null

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Fails {
    param([scriptblock]$Action, [string]$Pattern)
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected '$Pattern'; received '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected failure matching '$Pattern'."
}

function File-Pin {
    param([string]$Source, [string]$ArchivePath, [string]$StagedPath, [string]$Machine = '')
    $pin = [ordered]@{
        archivePath = $ArchivePath
        length = (Get-Item -LiteralPath $Source).Length
        sha256 = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($StagedPath) { $pin.stagedPath = $StagedPath }
    if ($Machine) { $pin.peMachine = $Machine }
    return [pscustomobject]$pin
}

function Save-Pin {
    param($Fixture)
    [IO.File]::WriteAllText(
        (Join-Path $Fixture.Root 'mxc-runtime.lock.json'),
        ($Fixture.Pin | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Refresh-ArchiveHash {
    param($Fixture)
    $hash = (Get-FileHash -LiteralPath $Fixture.Archive -Algorithm SHA512).Hash
    $Fixture.Pin.archiveSha512 = [Convert]::ToBase64String([Convert]::FromHexString($hash))
    Save-Pin $Fixture
}

function New-Fixture {
    $root = Join-Path $testRoot "repo with spaces $([guid]::NewGuid().ToString('N'))"
    $package = Join-Path $root 'package'
    $scripts = Join-Path $root 'scripts'
    New-Item -Path $package, $scripts -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Get-MxcRuntime.ps1') `
        -Destination (Join-Path $scripts 'Get-MxcRuntime.ps1')
    [IO.File]::WriteAllText((Join-Path $root 'Directory.Packages.props'),
        '<Project><ItemGroup><PackageVersion Include="Microsoft.Mxc.Sdk" Version="1.0.0" /></ItemGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $package 'microsoft.mxc.sdk.nuspec'),
        '<package><metadata><id>Microsoft.Mxc.Sdk</id><version>1.0.0</version></metadata></package>')
    $assembly = Join-Path $package 'lib\net8.0\Microsoft.Mxc.Sdk.dll'
    New-Item -Path (Split-Path $assembly -Parent) -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText($assembly, 'fixture managed assembly')
    [IO.File]::WriteAllText((Join-Path $package 'LICENSE.md'), 'fixture license')
    $architectures = @{}
    foreach ($architecture in @('x64', 'arm64')) {
        $machine = if ($architecture -eq 'x64') { 0x8664 } else { 0xaa64 }
        $directory = Join-Path $package "runtimes\win-$architecture\native"
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
        $files = @(foreach ($name in @('mxc_ffi.dll', 'plm.exe', 'wslcsdk.dll', 'wxc-wslc-daemon.exe')) {
            $path = Join-Path $directory $name
            $bytes = [byte[]]::new(128)
            $bytes[0] = 0x4d; $bytes[1] = 0x5a
            $bytes[0x3c] = 0x40
            $bytes[0x40] = 0x50; $bytes[0x41] = 0x45
            $bytes[0x44] = $machine -band 0xff
            $bytes[0x45] = $machine -shr 8
            [IO.File]::WriteAllBytes($path, $bytes)
            File-Pin $path "runtimes/win-$architecture/native/$name" $name ('0x{0:x4}' -f $machine)
        })
        $architectures[$architecture] = @{ files = $files }
    }
    $archive = Join-Path $root 'fixture.nupkg'
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $archive)
    Copy-Item -LiteralPath $archive -Destination (Join-Path $package 'microsoft.mxc.sdk.1.0.0.nupkg')
    $fixture = @{
        Root = $root
        Package = $package
        Archive = Join-Path $package 'microsoft.mxc.sdk.1.0.0.nupkg'
        Script = Join-Path $scripts 'Get-MxcRuntime.ps1'
        Output = Join-Path $root 'verified'
        Pin = [pscustomobject]@{
            package = 'Microsoft.Mxc.Sdk'
            version = '1.0.0'
            archiveSha512 = ''
            managedAssembly = File-Pin $assembly 'lib/net8.0/Microsoft.Mxc.Sdk.dll' ''
            architectures = $architectures
            licenseFiles = @(File-Pin (Join-Path $package 'LICENSE.md') 'LICENSE.md' 'mxc-LICENSE.md')
        }
    }
    Refresh-ArchiveHash $fixture
    return $fixture
}

function Verify-Fixture {
    param($Fixture, [string]$Architecture = 'x64', [string]$RuntimeDirectory = '')
    & $Fixture.Script -Architecture $Architecture -PackageDirectory $Fixture.Package `
        -OutputDirectory $Fixture.Output -RuntimeDirectory $RuntimeDirectory
}

function Add-ArchiveEntry {
    param($Fixture, [string]$Path)
    $zip = [IO.Compression.ZipFile]::Open($Fixture.Archive, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.CreateEntry($Path)
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('unexpected bytes') } finally { $writer.Dispose() }
    }
    finally { $zip.Dispose() }
    Refresh-ArchiveHash $Fixture
}

try {
    foreach ($architecture in @('x64', 'arm64')) {
        $f = New-Fixture
        $result = Verify-Fixture $f $architecture
        Assert-True ($result.Files.Count -eq 6) 'The SDK inventory omitted native/license/provenance files.'
        Assert-True ($result.Provenance.architecture -ceq $architecture) 'SDK provenance named the wrong architecture.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $f.Output 'mxc_ffi.dll'))) `
            'The verifier became a second native-runtime staging owner.'
        $publish = Join-Path $f.Root 'publish'
        New-Item -Path $publish -ItemType Directory | Out-Null
        foreach ($file in $result.Files) {
            Copy-Item -LiteralPath $file.sourcePath -Destination (Join-Path $publish $file.path)
        }
        Verify-Fixture $f $architecture $publish | Out-Null
        [IO.File]::WriteAllText((Join-Path $publish 'plm.exe'), 'tampered')
        Assert-Fails { Verify-Fixture $f $architecture $publish } 'integrity pin'
    }

    $f = New-Fixture
    [IO.File]::WriteAllText((Join-Path $f.Root 'Directory.Packages.props'),
        '<Project><ItemGroup><PackageVersion Include="Microsoft.Mxc.Sdk" Version="2.0.0" /></ItemGroup></Project>')
    Assert-Fails { Verify-Fixture $f } 'central SDK package version'

    $f = New-Fixture
    [IO.File]::AppendAllText($f.Archive, 'archive tamper')
    Assert-Fails { Verify-Fixture $f } 'archive.*integrity pin'
    Assert-True (-not (Test-Path -LiteralPath $f.Output)) 'A failed archive check published metadata.'

    $f = New-Fixture
    [IO.File]::WriteAllText((Join-Path $f.Package 'lib\net8.0\Microsoft.Mxc.Sdk.dll'), 'assembly tamper')
    Assert-Fails { Verify-Fixture $f } 'file.*integrity pin'

    $f = New-Fixture
    Remove-Item -LiteralPath (Join-Path $f.Package 'runtimes\win-x64\native\wslcsdk.dll')
    Assert-Fails { Verify-Fixture $f } 'file.*integrity pin'

    $f = New-Fixture
    $f.Pin.architectures.x64.files[0].stagedPath = '..\escape.dll'
    Save-Pin $f
    Assert-Fails { Verify-Fixture $f } 'Unsafe MXC inventory path'

    $f = New-Fixture
    $f.Pin.architectures.x64.files += $f.Pin.architectures.x64.files[0]
    Save-Pin $f
    Assert-Fails { Verify-Fixture $f } 'duplicate'

    $f = New-Fixture
    $f.Pin.architectures.x64.files[0].peMachine = '0xaa64'
    Save-Pin $f
    Assert-Fails { Verify-Fixture $f } 'selected architecture'

    $f = New-Fixture
    Add-ArchiveEntry $f 'runtimes/win-x64/native/mxc_ffi.dll'
    Assert-Fails { Verify-Fixture $f } 'duplicate path'

    $f = New-Fixture
    Add-ArchiveEntry $f 'runtimes/win-x64/native/../escape.dll'
    Assert-Fails { Verify-Fixture $f } 'Unsafe MXC inventory path'

    $f = New-Fixture
    Add-ArchiveEntry $f 'runtimes/win-x64/native/unverified.dll'
    Assert-Fails { Verify-Fixture $f } 'unexpected native file set'

    $f = New-Fixture
    $result = Verify-Fixture $f
    $publish = Join-Path $f.Root 'publish'
    New-Item -Path $publish -ItemType Directory | Out-Null
    foreach ($file in $result.Files) {
        Copy-Item -LiteralPath $file.sourcePath -Destination (Join-Path $publish $file.path)
    }
    [IO.File]::WriteAllText((Join-Path $publish 'wxc-exec.exe'), 'retired executor')
    Assert-Fails { Verify-Fixture $f 'x64' $publish } 'unexpected or retired'

    Write-Host 'MXC SDK integrity tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
