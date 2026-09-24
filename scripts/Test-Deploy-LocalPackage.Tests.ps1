[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LocalPackage.psm1') -Force

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-local-package-$([guid]::NewGuid().ToString('N'))"
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

function Test-ProductionMxcStageAdapterIgnoresStaleNativeExitState {
    $fixture = Join-Path $testRoot "mxc adapter $([guid]::NewGuid().ToString('N'))"
    $scripts = Join-Path $fixture 'scripts'
    $output = Join-Path $fixture 'staged'
    New-Item -Path $scripts -ItemType Directory -Force | Out-Null
    New-Item -Path $output -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Get-MxcRuntime.ps1') `
        -Destination (Join-Path $scripts 'Get-MxcRuntime.ps1')

    $runtimeFile = Join-Path $output 'fixture.bin'
    [IO.File]::WriteAllText($runtimeFile, 'cached runtime')
    [IO.File]::WriteAllText((Join-Path $output 'mxc-runtime.json'), '{}')
    $sha = (Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $length = (Get-Item -LiteralPath $runtimeFile).Length
    $lock = @{
        version = 'test'
        architectures = @{
            x64 = @{
                files = @(@{
                    stagedPath = 'fixture.bin'
                    archivePath = 'package/fixture.bin'
                    length = $length
                    sha256 = $sha
                })
            }
        }
        licenseFiles = @()
    } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $fixture 'mxc-runtime.lock.json'), $lock)

    $module = Get-Module LocalPackage
    $adapter = & $module { (Get-LocalPackageOperations).StageMxcRuntime }
    & $env:ComSpec /d /c 'exit 23'
    Assert-True ($LASTEXITCODE -eq 23) 'The test must establish stale native failure state.'

    & $adapter $fixture 'x64' $output
    Assert-True ((Test-Path -LiteralPath $runtimeFile -PathType Leaf)) `
        'Cached MXC staging should succeed regardless of prior native exit state.'
    $global:LASTEXITCODE = 0
}

function New-Fixture {
    $root = Join-Path $testRoot "repo with spaces $([guid]::NewGuid().ToString('N'))"
    $project = Join-Path $root 'src\OpenClaw.Launcher'
    New-Item -Path (Join-Path $project 'Images') -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $project 'Images\StoreLogo.png'), 'fixture image')
    New-Item -Path (Join-Path $project 'node') -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $project 'node\native-redirect.mjs'), 'fixture redirect')
    [IO.File]::WriteAllText((Join-Path $project 'Program.cs'), 'launcher source')
    New-Item -Path (Join-Path $root 'src\OpenClaw.SessionHost') -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'src\OpenClaw.SessionHost\Program.cs'), 'session host source')
    New-Item -Path (Join-Path $root 'src\OpenClaw.SessionProtocol') -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'src\OpenClaw.SessionProtocol\Protocol.cs'), 'protocol source')
    [IO.File]::WriteAllText((Join-Path $project 'Package.appxmanifest'), @'
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
  <Identity Name="OpenClawFoundation.OpenClawGateway" Publisher="CN=Fixture" Version="0.0.0.0" />
  <Properties>
    <DisplayName>OpenClaw Gateway</DisplayName>
  </Properties>
  <Applications>
    <Application Id="App" Executable="openclaw.exe">
      <uap:VisualElements DisplayName="OpenClaw Gateway" />
      <Extensions>
        <uap5:Extension Category="windows.appExecutionAlias" Executable="openclaw.exe">
          <uap5:AppExecutionAlias>
            <uap5:ExecutionAlias Alias="openclaw.exe" />
          </uap5:AppExecutionAlias>
        </uap5:Extension>
      </Extensions>
    </Application>
    <Application Id="Control" Executable="openclaw.exe">
      <uap:VisualElements DisplayName="OpenClaw Gateway" />
      <Extensions>
        <uap5:Extension Category="windows.appExecutionAlias" Executable="openclaw.exe">
          <uap5:AppExecutionAlias>
            <uap5:ExecutionAlias Alias="clawctl.exe" />
          </uap5:AppExecutionAlias>
        </uap5:Extension>
      </Extensions>
    </Application>
  </Applications>
</Package>
'@)

    $state = @{
        Root = $root
        Installed = $null
        Now = [datetime]'2026-09-14T12:00:00'
        RunId = [long]500
        Offline = $false
        PayloadText = 'first payload'
        NodeVersion = '24.20.0'
        DownloadFailure = $false
        PublishFailure = $false
        SkipHost = $false
        HostVersion = 1
        RegisterFailure = $false
        BadRegistration = $false
        Queries = 0
        PackageQueries = @()
        Downloads = 0
        RuntimeDownloads = 0
        MxcStages = 0
        Publishes = 0
        Registrations = 0
        Setups = 0
        SetupPackageNames = @()
        SetupPackageFamilyNames = @()
        SetupFailure = $false
        Removals = @()
        PreserveFlags = @()
        PublishMetadata = $null
        LauncherPublishes = @()
        Commit = '1111111111111111111111111111111111111111'
    }
    $state.WritePayload = {
        param($directory, $architecture, $text, $nodeVersion)
        New-Item -Path (Join-Path $directory 'app') -ItemType Directory -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $directory 'app\openclaw.mjs'), $text)
        $completionDirectory = Join-Path $directory 'app\shell-completions'
        New-Item -Path $completionDirectory -ItemType Directory -Force | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $completionDirectory 'openclaw.ps1'),
            'Register-ArgumentCompleter -Native -CommandName openclaw')
        [IO.File]::WriteAllText((Join-Path $directory 'payload-metadata.json'), (@{
            architecture = $architecture
            layout = 'expanded-directory'
            nodeVersion = $nodeVersion
            packageVersion = '2026.9.4'
            resolvedCommit = '0965053fe6b9341776df147a6934b7485c60b5ca'
        } | ConvertTo-Json))
    }
    $state.Operations = @{
        Now = { $state.Now }.GetNewClosure()
        NativeArchitecture = { 'x64' }
        Preflight = { param($architecture) return $null }
        LatestRun = {
            if ($state.Offline) { throw 'GitHub is unavailable.' }
            $state.Queries++
            return $state.RunId
        }.GetNewClosure()
        Download = {
            param($runId, $architecture, $directory)
            if ($state.Offline) { throw 'Downloads are unavailable.' }
            $state.Downloads++
            & $state.WritePayload $directory $architecture $state.PayloadText $state.NodeVersion
            if ($state.DownloadFailure) { throw 'Interrupted payload download.' }
            return $null
        }.GetNewClosure()
        DownloadRuntime = {
            param($nodeVersion, $name, $path)
            if ($state.Offline) { throw 'nodejs.org is unavailable.' }
            $state.RuntimeDownloads++
            [IO.File]::WriteAllText($path, "fixture archive $name")
            return $null
        }.GetNewClosure()
        TestArchive = { param($path, $expectedRoot) return $null }
        StageMxcRuntime = {
            param($repositoryRoot, $architecture, $output)
            $state.MxcStages++
            New-Item -Path $output -ItemType Directory -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $output 'wxc-exec.exe'), 'fixture executor')
            [IO.File]::WriteAllText((Join-Path $output 'plm.exe'), 'fixture lifecycle')
            [IO.File]::WriteAllText((Join-Path $output 'mxc-runtime.json'), '{}')
            return $null
        }.GetNewClosure()
        CheckoutCommit = { $state.Commit }.GetNewClosure()
        Publish = {
            param($project, $architecture, $output, $metadata)
            $state.Publishes++
            if ($project -like '*OpenClaw.Launcher.csproj') {
                $state.PublishMetadata = $metadata
                $state.LauncherPublishes += $metadata.PackageVersion
            }
            if ($state.PublishFailure) { throw 'NativeAOT publish failed.' }
            # Like MSBuild, every publish rewrites the project's intermediate
            # and output directories, whether or not anything changed.
            $projectDirectory = Split-Path $project -Parent
            foreach ($buildOutput in @('bin', 'obj')) {
                New-Item -Path (Join-Path $projectDirectory $buildOutput) -ItemType Directory -Force | Out-Null
                [IO.File]::WriteAllText(
                    (Join-Path $projectDirectory "$buildOutput\build.cache"),
                    [guid]::NewGuid().ToString())
            }
            New-Item -Path $output -ItemType Directory -Force | Out-Null
            # The binaries are a pure function of their inputs, as a real
            # deterministic publish is: the project and protocol sources, the
            # compile-time identity in metadata, and HostVersion, which models
            # an input the deployment cannot see, such as an SDK update.
            $sources = @(
                Get-Content -LiteralPath (Join-Path $projectDirectory 'Program.cs') -Raw
                Get-Content -LiteralPath (
                    Join-Path $state.Root 'src\OpenClaw.SessionProtocol\Protocol.cs') -Raw
            ) -join '|'
            $isSessionHost = $project -like '*OpenClaw.SessionHost*'
            if ($isSessionHost) {
                [IO.File]::WriteAllText(
                    (Join-Path $output 'openclaw-session-host.exe'),
                    "session host $($state.HostVersion) [$sources]")
            }
            elseif (-not $state.SkipHost) {
                $identity = if ($null -eq $metadata) {
                    ''
                }
                else {
                    $metadata | ConvertTo-Json -Compress
                }
                [IO.File]::WriteAllText(
                    (Join-Path $output 'openclaw.exe'),
                    "host $($state.HostVersion) [$sources] $identity")
            }
            return $null
        }.GetNewClosure()
        GetPackage = {
            param($name)
            $state.PackageQueries += $name
            # Installed holds every registered identity, so a patched
            # deployment can be observed beside the base package.
            return @($state.Installed) |
                Where-Object { $null -ne $_ -and $_.Name -ceq $name } |
                Select-Object -First 1
        }.GetNewClosure()
        RegisterPackage = {
            param($manifestPath)
            $state.Registrations++
            if ($state.RegisterFailure) { throw '0x80073CFB: registration blocked.' }
            [xml]$m = Get-Content -LiteralPath $manifestPath -Raw
            $name = [string]$m.Package.Identity.Name
            $registered = [pscustomobject]@{
                Name = $name
                Version = if ($state.BadRegistration) { '9.9.9.9' } else { $m.Package.Identity.Version }
                PackageFullName = "$($name)_$($m.Package.Identity.Version)_fixture"
                PackageFamilyName = "$($name)_fixture"
                InstallLocation = Split-Path $manifestPath -Parent
                IsDevelopmentMode = $true
                Status = 'Ok'
            }
            $state.Installed = @(
                @($state.Installed) | Where-Object { $null -ne $_ -and $_.Name -cne $name }
            ) + $registered
            return $null
        }.GetNewClosure()
        RemovePackage = {
            param($packageFullName, $preserveData)
            $state.Removals += $packageFullName
            $state.PreserveFlags += [bool]$preserveData
            $state.Installed = @(
                @($state.Installed) |
                    Where-Object { $null -ne $_ -and $_.PackageFullName -ne $packageFullName }
            )
            return $null
        }.GetNewClosure()
        TestPath = { param($path) Test-Path -LiteralPath $path }
        RunSetup = {
            param($packageName, $packageFamilyName)
            $state.Setups++
            $state.SetupPackageNames += $packageName
            $state.SetupPackageFamilyNames += $packageFamilyName
            if ($state.SetupFailure) { throw 'clawctl setup failed (exit 1).' }
            return $null
        }.GetNewClosure()
    }
    return $state
}

function Invoke-Fixture {
    param([hashtable]$State, [hashtable]$Arguments = @{})
    Invoke-LocalPackageDeployment -RepositoryRoot $State.Root -Operations $State.Operations @Arguments
}

try {
    # Clean checkout to a registered package.
    $f = New-Fixture
    $first = Invoke-Fixture $f
    Assert-True ($first.Changed -and $f.Downloads -eq 1 -and $f.RuntimeDownloads -eq 1 -and
        $f.MxcStages -eq 1 -and $f.Publishes -eq 2 -and
        $f.Registrations -eq 1) 'First deployment did not acquire, build, and register exactly once.'
    Assert-True ($f.Setups -eq 1) 'Deployment did not leave the package runnable by preparing the runtime.'
    Assert-True (
        $f.PublishMetadata.PackageVersion -eq $first.Version -and
        $f.PublishMetadata.PayloadVersion -eq '2026.9.4' -and
        $f.PublishMetadata.PayloadCommit -eq '0965053fe6b9341776df147a6934b7485c60b5ca'
    ) 'Launcher publish did not receive the selected local package and payload identity.'
    $localPackageModule = Get-Module LocalPackage
    Assert-True (
        (& $localPackageModule {
            Get-LocalPackageCheckoutCommit -FindGit { $null }
        }) -eq ''
    ) 'Missing Git should leave optional checkout metadata empty.'
    Assert-True (@($f.SetupPackageFamilyNames)[0] -eq 'OpenClawFoundation.OpenClawGateway_fixture') 'Setup did not target the owning package family.'
    Assert-True (@($first).Count -eq 1 -and $first.PackageFullName) 'Deployment did not return a single registration record.'
    $layout = $first.LayoutDirectory
    Assert-True ((Get-Content (Join-Path $layout 'app\openclaw.mjs') -Raw) -eq 'first payload') 'Layout does not expose the payload application.'
    Assert-True ((Get-Item (Join-Path $layout 'app')).Attributes -band [IO.FileAttributes]::ReparsePoint) 'The layout copied the application instead of linking it.'
    Assert-True (Test-Path (Join-Path $layout 'openclaw.exe')) 'Layout is missing the launcher.'
    Assert-True (Test-Path (Join-Path $layout 'node\native-redirect.mjs')) `
        'Layout is missing the native redirect script.'
    Assert-True (
        Test-Path (Join-Path $layout 'session-host\x64\openclaw-session-host.exe')
    ) 'Layout is missing the session host.'
    Assert-True (
        Test-Path (Join-Path $layout 'mxc\x64\wxc-exec.exe')
    ) 'Layout is missing the MXC executor.'
    Assert-True (
        Test-Path (Join-Path $layout 'mxc\x64\plm.exe')
    ) 'Layout is missing the MXC lifecycle tool.'
    Assert-True (
        Test-Path (Join-Path $layout 'mxc\x64\mxc-runtime.json')
    ) 'Layout is missing MXC provenance.'
    Assert-True (Test-Path (Join-Path $layout 'Images\StoreLogo.png')) 'Layout is missing package images.'
    Assert-True (@(Get-ChildItem (Join-Path $layout 'runtime') -File).Name -eq 'node-v24.20.0-win-x64.zip') 'Layout is missing the bundled Node.js runtime.'
    [xml]$m = Get-Content (Join-Path $layout 'AppxManifest.xml') -Raw
    Assert-True ($m.Package.Identity.Version -eq $first.Version -and
        $m.Package.Identity.ProcessorArchitecture -eq 'x64') 'Manifest identity was not stamped.'

    # Idempotent: nothing changed, nothing done, and no network.
    $f.Offline = $true
    $second = Invoke-Fixture $f
    Assert-True (-not $second.Changed) 'A no-change re-run reported work.'
    Assert-True ($f.Registrations -eq 1 -and $f.Downloads -eq 1 -and $f.RuntimeDownloads -eq 1) 'A no-change re-run re-registered or re-downloaded.'
    Assert-True ($second.Version -eq $first.Version) 'A no-change re-run altered the version.'
    $forced = Invoke-Fixture $f @{ Force = $true }
    Assert-True ($forced.Changed -and $f.Registrations -eq 2) '-Force did not re-register.'
    Assert-True ([version]$forced.Version -gt [version]$first.Version) 'Re-registration did not advance the version.'
    # Registering over a damaged development registration does not repair it, so
    # the previous one must be removed first, preserving app data.
    Assert-True (@($f.Removals).Count -eq 1 -and @($f.PreserveFlags)[0] -eq $true) 'Re-registration did not first remove the existing development registration.'

    # A changed launcher must reach the registered package even when it changed
    # through an input the deployment does not hash, such as an SDK update. The
    # build inputs only predict the version; the published binaries decide.
    $f.Now = $f.Now.AddMinutes(5)
    $f.HostVersion = 2
    $publishedBefore = @($f.LauncherPublishes).Count
    $changed = Invoke-Fixture $f
    Assert-True ($changed.Changed) 'A launcher change was not detected.'
    $changedLauncher = Get-Content (Join-Path $layout 'openclaw.exe') -Raw
    Assert-True (
        $changedLauncher.StartsWith('host 2 ', [StringComparison]::Ordinal) -and
        $changedLauncher.Contains(
            "`"PackageVersion`":`"$($changed.Version)`"",
            [StringComparison]::Ordinal)
    ) 'The layout kept a stale launcher or package identity.'
    Assert-True (
        (@($f.LauncherPublishes | Select-Object -Skip $publishedBefore) -join ',') -eq
            "$($forced.Version),$($changed.Version)"
    ) 'Binaries that changed with unchanged inputs were not verified, then rebuilt with the new version.'

    # A changed deployment must publish the launcher once, with the version it
    # registers: the package version is compiled in, so every extra publish
    # is a full NativeAOT compile. Every change must still be deployed, and
    # the next run must settle although each publish rewrites bin and obj.
    $sp = New-Fixture
    $spFirst = Invoke-Fixture $sp
    Assert-True ((@($sp.LauncherPublishes) -join ',') -eq $spFirst.Version) `
        'A first deployment did not publish the launcher once with its version.'
    $spLauncher = Join-Path $spFirst.LayoutDirectory 'openclaw.exe'
    $spSessionHost = Join-Path $spFirst.LayoutDirectory 'session-host\x64\openclaw-session-host.exe'
    $sourceEdits = @(
        @{
            Name = 'launcher source'; Binary = $spLauncher; Marker = 'edited launcher source'
            Apply = {
                [IO.File]::WriteAllText(
                    (Join-Path $sp.Root 'src\OpenClaw.Launcher\Program.cs'), 'edited launcher source')
            }
        }
        @{
            Name = 'session host source'; Binary = $spSessionHost; Marker = 'edited session host source'
            Apply = {
                [IO.File]::WriteAllText(
                    (Join-Path $sp.Root 'src\OpenClaw.SessionHost\Program.cs'), 'edited session host source')
            }
        }
        @{
            Name = 'protocol source'; Binary = $spLauncher; Marker = 'edited protocol source'
            Apply = {
                [IO.File]::WriteAllText(
                    (Join-Path $sp.Root 'src\OpenClaw.SessionProtocol\Protocol.cs'), 'edited protocol source')
            }
        }
        @{
            Name = 'checkout commit'; Binary = $spLauncher
            Marker = '"PackageCommit":"2222222222222222222222222222222222222222"'
            Apply = { $sp.Commit = '2222222222222222222222222222222222222222' }
        }
        @{
            Name = 'central package version'; Binary = $null; Marker = $null
            Apply = {
                [IO.File]::WriteAllText((Join-Path $sp.Root 'Directory.Packages.props'), '<Project />')
            }
        }
    )
    foreach ($edit in $sourceEdits) {
        $sp.Now = $sp.Now.AddMinutes(1)
        & $edit.Apply
        $publishedBefore = @($sp.LauncherPublishes).Count
        $edited = Invoke-Fixture $sp
        $published = @($sp.LauncherPublishes | Select-Object -Skip $publishedBefore)
        [xml]$editedManifest = Get-Content (Join-Path $edited.LayoutDirectory 'AppxManifest.xml') -Raw
        Assert-True $edited.Changed "A $($edit.Name) change was reported as up to date."
        Assert-True (($published -join ',') -eq $edited.Version) (
            "A $($edit.Name) change published the launcher with [$($published -join ', ')] " +
            "instead of once with $($edited.Version).")
        Assert-True (
            $editedManifest.Package.Identity.Version -eq $edited.Version -and
            (Get-Content $spLauncher -Raw).Contains("`"PackageVersion`":`"$($edited.Version)`"")
        ) "A $($edit.Name) change registered a launcher compiled with another package version."
        if ($edit.Binary) {
            Assert-True ((Get-Content $edit.Binary -Raw).Contains($edit.Marker)) `
                "A $($edit.Name) change did not reach the registered layout."
        }

        $registrationsBefore = $sp.Registrations
        $publishedBefore = @($sp.LauncherPublishes).Count
        $settled = Invoke-Fixture $sp
        Assert-True (
            -not $settled.Changed -and $settled.Version -eq $edited.Version -and
            $sp.Registrations -eq $registrationsBefore
        ) "The deployment after a $($edit.Name) change did not settle."
        Assert-True (
            (@($sp.LauncherPublishes | Select-Object -Skip $publishedBefore) -join ',') -eq $edited.Version
        ) "An unchanged deployment after a $($edit.Name) change published the launcher with a new version."
    }

    # Rebuilding a supplied payload in place changes the identity compiled into
    # the launcher, so it is a changed input too.
    $pd = New-Fixture
    $pdPayload = Join-Path $testRoot "rebuilt supplied payload $([guid]::NewGuid().ToString('N'))"
    & $pd.WritePayload $pdPayload 'x64' 'supplied' '24.20.0'
    Invoke-Fixture $pd @{ PayloadDirectory = $pdPayload } | Out-Null
    $pdMetadataPath = Join-Path $pdPayload 'payload-metadata.json'
    $pdMetadata = Get-Content -LiteralPath $pdMetadataPath -Raw | ConvertFrom-Json
    $pdMetadata.packageVersion = '2026.9.5'
    [IO.File]::WriteAllText($pdMetadataPath, ($pdMetadata | ConvertTo-Json))
    $pd.Now = $pd.Now.AddMinutes(1)
    $publishedBefore = @($pd.LauncherPublishes).Count
    $pdChanged = Invoke-Fixture $pd @{ PayloadDirectory = $pdPayload }
    Assert-True (
        $pdChanged.Changed -and
        (@($pd.LauncherPublishes | Select-Object -Skip $publishedBefore) -join ',') -eq $pdChanged.Version -and
        $pd.PublishMetadata.PayloadVersion -eq '2026.9.5'
    ) 'A rebuilt supplied payload was not published once with its new identity.'

    # A patched identity publishes once too, under its own identity and
    # version, and leaves the base registration from the same checkout alone.
    $pp = New-Fixture
    $ppBase = Invoke-Fixture $pp
    $ppPatch = Invoke-Fixture $pp @{ Patch = 'foo' }
    [IO.File]::WriteAllText((Join-Path $pp.Root 'src\OpenClaw.Launcher\Program.cs'), 'patched launcher source')
    $pp.Now = $pp.Now.AddMinutes(1)
    $publishedBefore = @($pp.LauncherPublishes).Count
    $ppChanged = Invoke-Fixture $pp @{ Patch = 'foo' }
    [xml]$ppManifest = Get-Content (Join-Path $ppChanged.LayoutDirectory 'AppxManifest.xml') -Raw
    Assert-True (
        $ppChanged.Changed -and
        (@($pp.LauncherPublishes | Select-Object -Skip $publishedBefore) -join ',') -eq $ppChanged.Version
    ) 'A changed patched deployment did not publish the launcher once with its version.'
    Assert-True (
        $ppManifest.Package.Identity.Name -ceq 'OpenClawFoundation.OpenClawGateway-foo' -and
        $ppManifest.Package.Identity.Version -eq $ppChanged.Version -and
        [version]$ppChanged.Version -gt [version]$ppPatch.Version -and
        (Get-Content (Join-Path $ppChanged.LayoutDirectory 'openclaw.exe') -Raw).Contains(
            "`"PackageVersion`":`"$($ppChanged.Version)`"")
    ) 'A changed patch did not keep its identity or register the version it compiled.'
    Assert-True (
        (@($pp.Installed) | Where-Object Name -ceq 'OpenClawFoundation.OpenClawGateway').PackageFullName -eq
            $ppBase.PackageFullName
    ) 'A patched deployment changed the base registration.'
    Assert-True (-not (Invoke-Fixture $pp @{ Patch = 'foo' }).Changed) 'A changed patch did not settle.'
    $ppBaseChanged = Invoke-Fixture $pp
    Assert-True (
        $ppBaseChanged.Changed -and [version]$ppBaseChanged.Version -gt [version]$ppBase.Version
    ) 'The base identity was reported current after the source it was built from changed.'

    # A checkout that deployed before build-input fingerprints existed has a
    # state record without one. Its first deployment must take the ordinary
    # changed-deployment path, removing the registration with its app data
    # preserved exactly as a source change does, and then settle.
    $delta = {
        param($state, $before)
        [pscustomobject]@{
            Removals = @($state.Removals | Select-Object -Skip $before.Removals)
            PreserveFlags = @($state.PreserveFlags | Select-Object -Skip $before.Removals)
            Registrations = $state.Registrations - $before.Registrations
            Setups = $state.Setups - $before.Setups
            LauncherPublishes = @($state.LauncherPublishes | Select-Object -Skip $before.LauncherPublishes)
        }
    }
    $snapshot = {
        param($state)
        @{
            Removals = @($state.Removals).Count; Registrations = $state.Registrations
            Setups = $state.Setups; LauncherPublishes = @($state.LauncherPublishes).Count
        }
    }
    $control = New-Fixture
    $controlPrior = Invoke-Fixture $control
    [IO.File]::WriteAllText((Join-Path $control.Root 'src\OpenClaw.Launcher\Program.cs'), 'control source edit')
    $control.Now = $control.Now.AddMinutes(1)
    $controlBefore = & $snapshot $control
    $controlChanged = Invoke-Fixture $control
    $controlDelta = & $delta $control $controlBefore

    $pr = New-Fixture
    $priorDeployment = Invoke-Fixture $pr
    $priorStatePath = Join-Path $pr.Root 'artifacts\local-package\x64\state.json'
    $priorRecord = Get-Content -LiteralPath $priorStatePath -Raw | ConvertFrom-Json -AsHashtable
    $priorRecord.Remove('inputFingerprint')
    Assert-True (
        (@($priorRecord.Keys | Sort-Object) -join ',') -ceq
            'fingerprint,layoutDirectory,packageFullName,payloadDirectory,schemaVersion,setupComplete,version'
    ) 'The fixture does not reproduce the state record written before this change.'
    [IO.File]::WriteAllText($priorStatePath, ($priorRecord | ConvertTo-Json -Depth 8) + "`n")
    $pr.Now = $pr.Now.AddMinutes(1)
    $priorBefore = & $snapshot $pr
    $upgraded = Invoke-Fixture $pr
    $upgradeDelta = & $delta $pr $priorBefore
    Assert-True (
        $upgraded.Changed -and [version]$upgraded.Version -gt [version]$priorDeployment.Version -and
        ($upgradeDelta.LauncherPublishes -join ',') -eq $upgraded.Version
    ) 'A prior state record did not redeploy once with a new version.'
    Assert-True (
        ($upgradeDelta.Removals -join ',') -eq $priorDeployment.PackageFullName -and
        ($controlDelta.Removals -join ',') -eq $controlPrior.PackageFullName -and
        ($upgradeDelta.PreserveFlags -join ',') -eq ($controlDelta.PreserveFlags -join ',') -and
        ($upgradeDelta.PreserveFlags -join ',') -eq 'True' -and
        $upgradeDelta.Registrations -eq $controlDelta.Registrations -and $upgradeDelta.Registrations -eq 1 -and
        $upgradeDelta.Setups -eq $controlDelta.Setups -and $upgradeDelta.Setups -eq 1 -and
        $controlChanged.Changed
    ) 'Upgrading a prior state record did not use the same registration, removal, and app-data path as a changed deployment.'
    Assert-True (
        (Get-Content -LiteralPath $priorStatePath -Raw | ConvertFrom-Json -AsHashtable).ContainsKey('inputFingerprint')
    ) 'The upgraded deployment did not record its build inputs.'
    $settledBefore = & $snapshot $pr
    $upgradeSettled = Invoke-Fixture $pr
    $settledDelta = & $delta $pr $settledBefore
    Assert-True (
        -not $upgradeSettled.Changed -and $upgradeSettled.Version -eq $upgraded.Version -and
        $settledDelta.Registrations -eq 0 -and @($settledDelta.Removals).Count -eq 0
    ) 'The deployment after upgrading a prior state record did not settle.'

    # Payload refresh replaces content and retires the old generation only on success.
    $f.Offline = $false
    $f.PayloadText = 'refreshed payload'
    $refreshed = Invoke-Fixture $f @{ RefreshPayload = $true }
    Assert-True ($refreshed.Changed) 'Refresh reported no change.'
    Assert-True ((Get-Content (Join-Path $layout 'app\openclaw.mjs') -Raw) -eq 'refreshed payload') 'Refresh did not repoint the layout at new content.'
    Assert-True ($f.Downloads -eq 2) 'Refresh did not download.'
    $keptPayload = @(Get-ChildItem (Join-Path $f.Root 'artifacts\local-package\x64\payloads') -Directory).Count
    Assert-True ($keptPayload -eq 1) 'A successful refresh did not retire the superseded payload.'

    # A refresh that fails to register must keep the previous payload selected.
    $f.PayloadText = 'never registered'
    $f.RegisterFailure = $true
    $selectionPath = Join-Path $f.Root 'artifacts\local-package\x64\payloads\current.json'
    $selectedBefore = (Get-Content $selectionPath -Raw | ConvertFrom-Json).generation
    Assert-Fails { Invoke-Fixture $f @{ RefreshPayload = $true } } '0x80073CFB'
    Assert-True (@(Get-ChildItem (Join-Path $f.Root 'artifacts\local-package\x64\payloads') -Directory).Count -eq 2) 'A failed registration discarded the previous payload.'
    Assert-True ((Get-Content $selectionPath -Raw | ConvertFrom-Json).generation -eq $selectedBefore) 'A failed deployment left the unusable payload selected.'
    $f.RegisterFailure = $false

    # Conflicting packaged install.
    $g = New-Fixture
    $g.Installed = [pscustomobject]@{
        Name = 'OpenClawFoundation.OpenClawGateway'
        Version = '1.2.3.4'; PackageFullName = 'OpenClawFoundation.OpenClawGateway_1.2.3.4_x64__pkg'
        PackageFamilyName = 'OpenClawFoundation.OpenClawGateway_pkg'
        InstallLocation = 'C:\Program Files\WindowsApps\fake'; IsDevelopmentMode = $false; Status = 'Ok'
    }
    Assert-Fails { Invoke-Fixture $g } 'already installed from a package'
    Assert-True ($g.Registrations -eq 0 -and @($g.Removals).Count -eq 0) 'A conflicting packaged install was touched without consent.'
    $replaced = Invoke-Fixture $g @{ ReplaceExistingInstall = $true }
    Assert-True ($g.Removals -contains 'OpenClawFoundation.OpenClawGateway_1.2.3.4_x64__pkg' -and $replaced.Changed) 'Explicit replacement did not remove the packaged install.'
    # Removal happens first, so the dev build need not out-version the package it
    # replaced; staying on 0.1.x keeps a later real release installable.
    Assert-True ([version]$replaced.Version -lt [version]'1.0.0.0') 'A replacement build should not claim a release-range version.'

    # The legacy identity must be discovered by name before this checkout
    # mutates the shared layout. Its removal always requires explicit consent.
    $legacyLoose = New-Fixture
    $legacyLayout = Join-Path (
        $legacyLoose.Root
    ) 'artifacts\local-package\x64\layout'
    $legacyLoose.Installed = [pscustomobject]@{
        Name = 'OpenClaw.Gateway'
        Version = '0.1.0.0'
        PackageFullName = 'OpenClaw.Gateway_0.1.0.0_x64__legacy'
        PackageFamilyName = 'OpenClaw.Gateway_kaa03rpbbqef6'
        InstallLocation = $legacyLayout
        IsDevelopmentMode = $true
        Status = 'Ok'
    }
    Assert-Fails { Invoke-Fixture $legacyLoose } 'still registered'
    Assert-True (
        $legacyLoose.Downloads -eq 0 -and
        $legacyLoose.Registrations -eq 0 -and
        @($legacyLoose.Removals).Count -eq 0
    ) 'Deployment mutated state before obtaining consent to remove the legacy layout.'
    $legacyReplaced = Invoke-Fixture $legacyLoose @{
        ReplaceExistingInstall = $true
    }
    Assert-True (
        $legacyReplaced.Changed -and
        $legacyLoose.Removals -contains
            'OpenClaw.Gateway_0.1.0.0_x64__legacy' -and
        $legacyLoose.PreserveFlags -contains $true
    ) 'Explicit replacement did not remove the owned legacy loose registration.'

    $legacyPackaged = New-Fixture
    $legacyPackaged.Installed = [pscustomobject]@{
        Name = 'OpenClaw.Gateway'
        Version = '2026.9.403.0'
        PackageFullName = 'OpenClaw.Gateway_2026.9.403.0_x64__legacy'
        PackageFamilyName = 'OpenClaw.Gateway_kaa03rpbbqef6'
        InstallLocation = 'C:\Program Files\WindowsApps\legacy'
        IsDevelopmentMode = $false
        Status = 'Ok'
    }
    Assert-Fails { Invoke-Fixture $legacyPackaged } 'still registered'
    Invoke-Fixture $legacyPackaged @{
        ReplaceExistingInstall = $true
    } | Out-Null
    Assert-True (
        $legacyPackaged.Removals -contains
            'OpenClaw.Gateway_2026.9.403.0_x64__legacy' -and
        $legacyPackaged.PreserveFlags -contains $false
    ) 'Explicit replacement did not remove the legacy packaged install.'

    # Failure paths stop before registering.
    $h = New-Fixture
    $h.PublishFailure = $true
    Assert-Fails { Invoke-Fixture $h } 'publish failed'
    Assert-True ($h.Registrations -eq 0) 'A failed publish still registered.'
    $h.PublishFailure = $false
    $h.SkipHost = $true
    Assert-Fails { Invoke-Fixture $h } 'did not produce'
    Assert-True ($h.Registrations -eq 0) 'A missing launcher still registered.'
    $h.SkipHost = $false
    $h.BadRegistration = $true
    Assert-Fails { Invoke-Fixture $h } 'healthy local development build'
    $h.BadRegistration = $false
    $h.SetupFailure = $true
    Assert-Fails { Invoke-Fixture $h } 'clawctl setup failed'
    $h.SetupFailure = $false
    $h.HostVersion = 99
    $setupsBefore = $h.Setups
    $skipped = Invoke-Fixture $h @{ SkipSetup = $true }
    Assert-True ($skipped.Changed) '-SkipSetup did not deploy.'
    Assert-True ($h.Setups -eq $setupsBefore) '-SkipSetup still prepared the runtime.'

    $i = New-Fixture
    $i.DownloadFailure = $true
    Assert-Fails { Invoke-Fixture $i } 'Interrupted payload download'
    Assert-True ($i.Registrations -eq 0) 'A failed download still registered.'
    $i.Offline = $true
    Assert-Fails { Invoke-Fixture $i } 'unavailable|cache'

    # A cache selected before completion shipped must fail without disturbing
    # its registration or selection, then recover through an explicit refresh.
    $upgrade = New-Fixture
    $upgradeDeployment = Invoke-Fixture $upgrade
    $upgradeSelectionPath = Join-Path (
        $upgrade.Root
    ) 'artifacts\local-package\x64\payloads\current.json'
    $upgradeSelectionBefore = Get-Content -LiteralPath $upgradeSelectionPath -Raw
    $upgradeSelection = $upgradeSelectionBefore | ConvertFrom-Json
    $upgradePayload = Join-Path (
        Split-Path $upgradeSelectionPath
    ) $upgradeSelection.generation
    Remove-Item -LiteralPath (
        Join-Path $upgradePayload 'app\shell-completions\openclaw.ps1'
    )
    $upgradeRegistrations = $upgrade.Registrations

    Assert-Fails { Invoke-Fixture $upgrade } 'missing app\\shell-completions\\openclaw.ps1'
    Assert-True (
        $upgrade.Registrations -eq $upgradeRegistrations -and
        $upgrade.Installed.PackageFullName -eq $upgradeDeployment.PackageFullName
    ) 'An incompatible cached payload changed the working registration.'
    Assert-True (
        (Get-Content -LiteralPath $upgradeSelectionPath -Raw) -eq
        $upgradeSelectionBefore
    ) 'An incompatible cached payload changed the selected generation.'

    $upgrade.PayloadText = 'completion-compatible payload'
    $upgradeRecovered = Invoke-Fixture $upgrade @{ RefreshPayload = $true }
    $upgradeSelectionAfter = Get-Content -LiteralPath $upgradeSelectionPath -Raw
    Assert-True (
        $upgradeRecovered.Changed -and
        $upgrade.Registrations -eq ($upgradeRegistrations + 1)
    ) 'Refreshing an incompatible cached payload did not register its replacement.'
    Assert-True (
        $upgradeSelectionAfter -ne $upgradeSelectionBefore -and
        -not (Test-Path -LiteralPath $upgradePayload)
    ) 'Successful recovery did not select the replacement and retire the old payload.'

    # An MSIX composition owns a checkout-wide lock, composes the latest
    # successful payload by default, and downloads only a run its cache lacks.
    # A payload becomes the cached selection only after the composer accepts
    # it, so a rejected or interrupted download never becomes the default.
    $mc = New-Fixture
    $mc.Composed = @()
    $mc.ComposeFailure = $false
    $msixCache = Join-Path $mc.Root 'artifacts\local-msix\payloads\x64'
    $msixSelection = Join-Path $msixCache 'current.json'
    $compose = {
        param($directory)
        $mc.Composed += $directory
        if ($mc.ComposeFailure) { throw 'Payload metadata is not valid for this MSIX package.' }
    }.GetNewClosure()
    $composeMsix = {
        param([hashtable]$Arguments = @{})
        $msixArguments = @{ Architecture = 'x64' }
        foreach ($key in $Arguments.Keys) { $msixArguments[$key] = $Arguments[$key] }
        Invoke-LocalPackageMsixBuild -RepositoryRoot $mc.Root -Compose $compose `
            -Operations $mc.Operations @msixArguments
    }
    $selectedRun = { (Get-Content -LiteralPath $msixSelection -Raw | ConvertFrom-Json).runId }
    $readComposed = { Get-Content -LiteralPath (Join-Path @($mc.Composed)[-1] 'app\openclaw.mjs') -Raw }

    & $composeMsix
    $mcFirst = @($mc.Composed)[-1]
    Assert-True (
        $mc.Queries -eq 1 -and $mc.Downloads -eq 1 -and (& $readComposed) -eq 'first payload' -and
        (& $selectedRun) -eq 500
    ) 'The first composition did not download and select the latest run.'
    & $composeMsix
    Assert-True (
        $mc.Queries -eq 2 -and $mc.Downloads -eq 1 -and @($mc.Composed)[-1] -eq $mcFirst
    ) 'A composition of the unchanged latest run did not check it and reuse the cache.'

    $mc.RunId = [long]501
    $mc.PayloadText = 'newer main payload'
    & $composeMsix
    $mcNewer = @($mc.Composed)[-1]
    Assert-True (
        $mc.Downloads -eq 2 -and (& $readComposed) -eq 'newer main payload' -and
        (& $selectedRun) -eq 501
    ) 'A composition after a newer main run composed the stale cached payload.'
    Assert-True (
        -not (Test-Path -LiteralPath $mcFirst) -and
        @(Get-ChildItem -LiteralPath $msixCache -Directory).Count -eq 1
    ) 'An accepted newer payload did not retire the superseded generation.'

    # A pinned run the cache holds needs no GitHub. Without a pin, an
    # unreachable GitHub fails and names that path instead of silently
    # composing a cached payload that may be stale.
    $mc.Offline = $true
    & $composeMsix @{ PayloadRunId = [long]501 }
    Assert-True (
        $mc.Queries -eq 3 -and $mc.Downloads -eq 2 -and @($mc.Composed)[-1] -eq $mcNewer
    ) 'A composition pinned to the cached run contacted GitHub or downloaded again.'
    $composedBefore = @($mc.Composed).Count
    Assert-Fails { & $composeMsix } 'latest successful payload run.*-PayloadRunId 501.*-PayloadDirectory'
    Assert-True (
        @($mc.Composed).Count -eq $composedBefore -and (& $selectedRun) -eq 501
    ) 'An unreachable GitHub fell back to composing the cached payload.'
    $mc.Offline = $false

    # A payload the composer rejects must not become the cached selection, or
    # every later composition would repeat the failure.
    $mc.RunId = [long]502
    $mc.PayloadText = 'rejected payload'
    $mc.ComposeFailure = $true
    $selectionBefore = Get-Content -LiteralPath $msixSelection -Raw
    Assert-Fails { & $composeMsix } 'not valid for this MSIX package'
    $mcRejected = @($mc.Composed)[-1]
    Assert-True (
        (Get-Content -LiteralPath $msixSelection -Raw) -eq $selectionBefore -and
        -not (Test-Path -LiteralPath $mcRejected) -and (Test-Path -LiteralPath $mcNewer)
    ) 'A payload the composer rejected was selected or left in the cache.'
    $mc.ComposeFailure = $false
    $mc.PayloadText = 'accepted payload'
    & $composeMsix
    Assert-True (
        $mc.Downloads -eq 4 -and (& $readComposed) -eq 'accepted payload' -and (& $selectedRun) -eq 502 -and
        -not (Test-Path -LiteralPath $mcNewer)
    ) 'A retry after a rejected composition did not download, select, and retire as usual.'
    $mcAccepted = @($mc.Composed)[-1]

    $mc.PayloadText = 'refreshed payload'
    & $composeMsix @{ RefreshPayload = $true }
    Assert-True (
        $mc.Downloads -eq 5 -and (& $readComposed) -eq 'refreshed payload' -and
        -not (Test-Path -LiteralPath $mcAccepted) -and
        @(Get-ChildItem -LiteralPath $msixCache -Directory).Count -eq 1
    ) '-RefreshPayload did not replace the cached payload.'
    $mcRefreshed = @($mc.Composed)[-1]

    $mc.DownloadFailure = $true
    $composedBefore = @($mc.Composed).Count
    Assert-Fails { & $composeMsix @{ RefreshPayload = $true } } 'Interrupted payload download'
    $mc.DownloadFailure = $false
    Assert-True (
        @($mc.Composed).Count -eq $composedBefore -and (& $selectedRun) -eq 502 -and
        (@(Get-ChildItem -LiteralPath $msixCache -Directory).FullName -join '|') -eq $mcRefreshed
    ) 'An interrupted download was composed, selected, or left beside the cached payload.'

    # A run killed mid-download leaves its generation behind without selecting
    # it. The next run, holding the checkout, reclaims it instead of letting
    # abandoned payloads accumulate, and leaves anything it did not create.
    $abandoned = Join-Path $msixCache "503-$([guid]::NewGuid().ToString('N'))"
    & $mc.WritePayload $abandoned 'x64' 'abandoned download' '24.20.0'
    $unrelated = Join-Path $msixCache 'not-a-generation'
    New-Item -Path $unrelated -ItemType Directory | Out-Null
    $mc.Offline = $true
    & $composeMsix @{ PayloadRunId = [long]502 }
    $mc.Offline = $false
    Assert-True (
        -not (Test-Path -LiteralPath $abandoned) -and (Test-Path -LiteralPath $unrelated) -and
        @($mc.Composed)[-1] -eq $mcRefreshed -and (& $selectedRun) -eq 502
    ) 'An abandoned generation was not reclaimed, or the sweep touched what it did not create.'
    Remove-Item -LiteralPath $unrelated
    # Runs in one checkout share content\openclaw and this cache, so a second
    # run fails before touching either while the first holds the checkout.
    $mc.RunId = [long]503
    $downloadsBefore = $mc.Downloads
    $lockPath = Join-Path $mc.Root 'artifacts\local-msix\.lock'
    $held = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $selectionBefore = Get-Content -LiteralPath $msixSelection -Raw
        $queriesBefore = $mc.Queries
        $composedBefore = @($mc.Composed).Count
        Assert-Fails { & $composeMsix } 'Another Build-LocalMSIX\.ps1 run in this checkout holds .*\.lock'
        $suppliedPayload = Join-Path $testRoot 'locked supplied msix payload'
        & $mc.WritePayload $suppliedPayload 'x64' 'supplied msix payload' '24.20.0'
        Assert-Fails { & $composeMsix @{ PayloadDirectory = $suppliedPayload } } 'holds .*\.lock'
        Assert-True (
            $mc.Queries -eq $queriesBefore -and $mc.Downloads -eq $downloadsBefore -and
            @($mc.Composed).Count -eq $composedBefore -and
            (Get-Content -LiteralPath $msixSelection -Raw) -eq $selectionBefore -and
            (@(Get-ChildItem -LiteralPath $msixCache -Directory).FullName -join '|') -eq $mcRefreshed
        ) 'A run blocked by the checkout lock queried, downloaded, composed, or changed the cache.'
    }
    finally { $held.Dispose() }

    # A supplied payload is composed in place without GitHub or the cache.
    $mc.Offline = $true
    & $composeMsix @{ PayloadDirectory = $suppliedPayload }
    Assert-True (
        @($mc.Composed)[-1] -eq (Resolve-Path -LiteralPath $suppliedPayload).Path -and
        $mc.Downloads -eq $downloadsBefore -and (& $selectedRun) -eq 502
    ) 'A supplied payload was not composed directly or touched the cache.'
    $mc.Offline = $false
    Assert-Fails { & $composeMsix @{ PayloadDirectory = $suppliedPayload; RefreshPayload = $true } } 'cannot be combined'
    Assert-Fails { & $composeMsix @{ PayloadDirectory = $suppliedPayload; PayloadRunId = [long]7 } } 'cannot be combined'
    Assert-Fails { & $composeMsix @{ PayloadRunId = [long]-1 } } 'positive workflow run'

    # Payloads are architecture-specific, so arm64 composes from its own cache
    # and never from, or over, the x64 selection.
    $mc.PayloadText = 'arm64 payload'
    & $composeMsix @{ Architecture = 'arm64' }
    $arm64Metadata = Get-Content -LiteralPath (Join-Path @($mc.Composed)[-1] 'payload-metadata.json') -Raw |
        ConvertFrom-Json
    Assert-True (
        @($mc.Composed)[-1].StartsWith(
            (Join-Path $mc.Root 'artifacts\local-msix\payloads\arm64'), [StringComparison]::OrdinalIgnoreCase) -and
        $arm64Metadata.architecture -eq 'arm64' -and (& $selectedRun) -eq 502 -and
        (@(Get-ChildItem -LiteralPath $msixCache -Directory).FullName -join '|') -eq $mcRefreshed
    ) 'An arm64 composition used or changed the x64 payload cache.'

    # Argument guards.
    $j = New-Fixture
    $external = Join-Path $testRoot 'supplied payload with spaces'
    & $j.WritePayload $external 'x64' 'supplied' '24.20.0'
    $supplied = Invoke-Fixture $j @{ PayloadDirectory = $external }
    Assert-True ($j.Downloads -eq 0 -and $j.Queries -eq 0) 'A supplied payload still contacted GitHub.'
    Assert-True ((Get-Content (Join-Path $supplied.LayoutDirectory 'app\openclaw.mjs') -Raw) -eq 'supplied') 'A supplied payload was not used.'
    $incomplete = New-Fixture
    $incompletePayload = Join-Path $testRoot 'incomplete supplied payload'
    & $incomplete.WritePayload $incompletePayload 'x64' 'incomplete' '24.20.0'
    Remove-Item -LiteralPath (
        Join-Path $incompletePayload 'app\shell-completions\openclaw.ps1'
    )
    Assert-Fails {
        Invoke-Fixture $incomplete @{ PayloadDirectory = $incompletePayload }
    } 'missing app\\shell-completions\\openclaw.ps1.*-PayloadRunId'
    $legacy = New-Fixture
    $legacyPayload = Join-Path $testRoot 'legacy supplied payload'
    & $legacy.WritePayload $legacyPayload 'x64' 'legacy' '24.20.0'
    $legacyMetadataPath = Join-Path $legacyPayload 'payload-metadata.json'
    $legacyMetadata = Get-Content -LiteralPath $legacyMetadataPath -Raw | ConvertFrom-Json
    $legacyMetadata.PSObject.Properties.Remove('packageVersion')
    $legacyMetadata.PSObject.Properties.Remove('resolvedCommit')
    [IO.File]::WriteAllText(
        $legacyMetadataPath,
        ($legacyMetadata | ConvertTo-Json))
    Invoke-Fixture $legacy @{ PayloadDirectory = $legacyPayload } | Out-Null
    Assert-True (
        $legacy.PublishMetadata.PayloadVersion -eq 'unknown' -and
        $legacy.PublishMetadata.PayloadCommit -eq 'unknown'
    ) 'A legacy supplied payload did not use safe unknown identity fallbacks.'
    Assert-Fails { Invoke-Fixture $j @{ PayloadDirectory = $external; RefreshPayload = $true } } 'cannot be combined'
    Assert-Fails { Invoke-Fixture $j @{ PayloadDirectory = $external; PayloadRunId = [long]7 } } 'cannot be combined'
    Assert-Fails { Invoke-Fixture $j @{ PayloadRunId = [long]-1 } } 'positive workflow run'
    Assert-Fails {
        Invoke-LocalPackageDeployment -RepositoryRoot $j.Root -Operations @{ NotAnOperation = { } }
    } 'Unknown or invalid local package operation adapter'
    Assert-Fails {
        Invoke-LocalPackageDeployment -RepositoryRoot $j.Root -Operations @{ Preflight = 'not a scriptblock' }
    } 'Unknown or invalid local package operation adapter'

    # Unregister.
    $k = New-Fixture
    $deployed = Invoke-Fixture $k
    Remove-LocalPackageRegistration -RepositoryRoot $k.Root -Operations $k.Operations
    Assert-True ($k.Removals -contains $deployed.PackageFullName) 'Unregister did not remove the local registration.'
    Assert-True ($k.PreserveFlags -contains $true) 'Unregister discarded the package app data it could have preserved.'
    Assert-True (-not (Test-Path (Join-Path $k.Root 'artifacts\local-package\x64\state.json'))) 'Unregister left deployment state behind.'
    Assert-True (Test-Path (Join-Path $k.Root 'artifacts\local-package\x64\payloads')) 'Unregister discarded the payload cache.'
    $k.Installed = [pscustomobject]@{
        Name = 'OpenClawFoundation.OpenClawGateway'
        Version = '1.0.0.0'; PackageFullName = 'pkg'; InstallLocation = 'x'
        PackageFamilyName = 'OpenClawFoundation.OpenClawGateway_pkg'
        IsDevelopmentMode = $false; Status = 'Ok'
    }
    Assert-Fails { Remove-LocalPackageRegistration -RepositoryRoot $k.Root -Operations $k.Operations } 'not a local layout'

    $legacyUnregister = New-Fixture
    $legacyUnregisterState = Join-Path (
        $legacyUnregister.Root
    ) 'artifacts\local-package\x64'
    $legacyUnregisterLayout = Join-Path $legacyUnregisterState 'layout'
    New-Item -Path $legacyUnregisterLayout -ItemType Directory -Force |
        Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $legacyUnregisterState 'state.json'),
        '{}')
    $legacyUnregister.Installed = [pscustomobject]@{
        Name = 'OpenClaw.Gateway'
        Version = '0.1.0.0'
        PackageFullName = 'OpenClaw.Gateway_0.1.0.0_x64__legacy'
        PackageFamilyName = 'OpenClaw.Gateway_kaa03rpbbqef6'
        InstallLocation = $legacyUnregisterLayout
        IsDevelopmentMode = $true
        Status = 'Ok'
    }
    Remove-LocalPackageRegistration `
        -RepositoryRoot $legacyUnregister.Root `
        -Operations $legacyUnregister.Operations
    Assert-True (
        $legacyUnregister.PackageQueries -contains
            'OpenClawFoundation.OpenClawGateway' -and
        $legacyUnregister.PackageQueries -contains 'OpenClaw.Gateway' -and
        $legacyUnregister.Removals -contains
            'OpenClaw.Gateway_0.1.0.0_x64__legacy' -and
        $legacyUnregister.PreserveFlags -contains $true -and
        -not (Test-Path -LiteralPath (
            Join-Path $legacyUnregisterState 'state.json'
        ))
    ) 'Unregister did not safely remove the owned legacy loose registration.'

    # Version selection.
    Assert-True ((Get-LocalPackageNextVersion -InstalledVersion '2026.1.0.65535' -Now ([datetime]'2026-09-14')) -eq '2026.1.1.0') 'Revision rollover failed.'
    Assert-Fails { Get-LocalPackageNextVersion -InstalledVersion '65535.65535.65535.65535' } 'version space is exhausted'
    Assert-Fails { Get-LocalPackageNextVersion -InstalledVersion '1.2.3' } 'Invalid MSIX version'
    # A failed or skipped setup must not be recorded as a complete deployment.
    $s = New-Fixture
    $s.SetupFailure = $true
    Assert-Fails { Invoke-Fixture $s } 'clawctl setup failed'
    $s.SetupFailure = $false
    $recovered = Invoke-Fixture $s
    Assert-True ($recovered.Changed -and $s.Setups -ge 2) 'A run after a failed setup was short-circuited as up to date.'
    $afterRecovery = Invoke-Fixture $s
    Assert-True (-not $afterRecovery.Changed) 'A completed deployment did not settle.'

    $sk = New-Fixture
    $skipDeploy = Invoke-Fixture $sk @{ SkipSetup = $true }
    Assert-True ($sk.Setups -eq 0 -and $skipDeploy.Changed) '-SkipSetup prepared the runtime.'
    $afterSkip = Invoke-Fixture $sk
    Assert-True ($afterSkip.Changed -and $sk.Setups -eq 1) 'A run after -SkipSetup did not complete the setup it skipped.'

    # A damaged live layout must not be reported as up to date.
    foreach ($break in @('openclaw.exe', 'Images', 'app', 'runtime', 'node\native-redirect.mjs')) {
        $d = New-Fixture
        $deployed = Invoke-Fixture $d
        $target = Join-Path $deployed.LayoutDirectory $break
        Remove-Item -LiteralPath $target -Recurse -Force
        $repaired = Invoke-Fixture $d
        Assert-True ($repaired.Changed) "A layout missing '$break' was reported as up to date."
        Assert-True (Test-Path -LiteralPath $target) "A layout missing '$break' was not repaired."
    }

    $modifiedNodeScript = New-Fixture
    $modifiedDeployment = Invoke-Fixture $modifiedNodeScript
    $redirectScript = Join-Path $modifiedDeployment.LayoutDirectory 'node\native-redirect.mjs'
    [IO.File]::WriteAllText($redirectScript, 'corrupt redirect')
    $repairedNodeScript = Invoke-Fixture $modifiedNodeScript
    Assert-True $repairedNodeScript.Changed 'A modified redirect script was reported as up to date.'
    Assert-True ((Get-Content -LiteralPath $redirectScript -Raw) -eq 'fixture redirect') `
        'A modified redirect script was not repaired.'

    # The layout runtime copy is replaced, not trusted by name.
    $c = New-Fixture
    $cDeployed = Invoke-Fixture $c
    $archive = Get-ChildItem (Join-Path $cDeployed.LayoutDirectory 'runtime') -File | Select-Object -First 1
    [IO.File]::WriteAllText($archive.FullName, 'truncated')
    $c.HostVersion = 2
    Invoke-Fixture $c | Out-Null
    Assert-True ((Get-Content $archive.FullName -Raw) -ne 'truncated') 'A corrupt layout runtime archive survived redeployment.'

    # A development registration owned by another location is not taken over.
    $o = New-Fixture
    $o.Installed = [pscustomobject]@{
        Name = 'OpenClawFoundation.OpenClawGateway'
        Version = '0.1.0.0'; PackageFullName = 'OpenClawFoundation.OpenClawGateway_0.1.0.0_x64__other'
        PackageFamilyName = 'OpenClawFoundation.OpenClawGateway_other'
        InstallLocation = (Join-Path $testRoot 'someone elses layout')
        IsDevelopmentMode = $true; Status = 'Ok'
    }
    Assert-Fails { Invoke-Fixture $o } 'registered from another location'
    Assert-True ($o.Registrations -eq 0 -and @($o.Removals).Count -eq 0) 'A foreign registration was touched without consent.'
    Assert-Fails {
        Remove-LocalPackageRegistration -RepositoryRoot $o.Root -Operations $o.Operations
    } 'does not own that registration'
    $takenOver = Invoke-Fixture $o @{ ReplaceExistingInstall = $true }
    Assert-True ($takenOver.Changed) 'Explicit take-over did not register.'

    # A foreign registration whose version matches this checkout's retained
    # state satisfies every other up-to-date condition, so only an ownership
    # check stops the short circuit from silently skipping the takeover.
    $fo = New-Fixture
    $mine = Invoke-Fixture $fo
    $foreignLayout = Join-Path $testRoot 'other checkout layout'
    New-Item -Path $foreignLayout -ItemType Directory -Force | Out-Null
    $fo.Installed = [pscustomobject]@{
        Name = 'OpenClawFoundation.OpenClawGateway'
        Version = $mine.Version
        PackageFullName = "OpenClawFoundation.OpenClawGateway_$($mine.Version)_x64__other"
        PackageFamilyName = 'OpenClawFoundation.OpenClawGateway_other'
        InstallLocation = $foreignLayout
        IsDevelopmentMode = $true
        Status = 'Ok'
    }
    Assert-Fails { Invoke-Fixture $fo } 'registered from another location'
    $registrationsBefore = $fo.Registrations
    $reclaimed = Invoke-Fixture $fo @{ ReplaceExistingInstall = $true }
    Assert-True ($reclaimed.Changed) 'A same-version foreign registration was reported as up to date.'
    Assert-True ($fo.Registrations -eq $registrationsBefore + 1) 'Take-over did not actually register.'
    Assert-True (
        [IO.Path]::GetFullPath($fo.Installed.InstallLocation).TrimEnd('\') -ieq
        [IO.Path]::GetFullPath($mine.LayoutDirectory).TrimEnd('\')
    ) 'The aliases still resolve to the other checkout after take-over.'

    Test-ProductionMxcStageAdapterIgnoresStaleNativeExitState

    # A patched identity registers beside the base package under its own name,
    # aliases, display name, and state root. Base and legacy registrations that
    # would block a base deployment are neither consulted nor disturbed.
    $p = New-Fixture
    $p.Installed = @(
        [pscustomobject]@{
            Name = 'OpenClawFoundation.OpenClawGateway'
            Version = '1.2.3.4'; PackageFullName = 'OpenClawFoundation.OpenClawGateway_1.2.3.4_x64__pkg'
            PackageFamilyName = 'OpenClawFoundation.OpenClawGateway_pkg'
            InstallLocation = 'C:\Program Files\WindowsApps\fake'; IsDevelopmentMode = $false; Status = 'Ok'
        }
        [pscustomobject]@{
            Name = 'OpenClaw.Gateway'
            Version = '0.1.0.0'; PackageFullName = 'OpenClaw.Gateway_0.1.0.0_x64__legacy'
            PackageFamilyName = 'OpenClaw.Gateway_kaa03rpbbqef6'
            InstallLocation = (Join-Path $p.Root 'artifacts\local-package\x64\layout')
            IsDevelopmentMode = $true; Status = 'Ok'
        }
    )
    $patched = Invoke-Fixture $p @{ Patch = 'Foo' }
    Assert-True ($patched.Changed -and @($p.Removals).Count -eq 0) `
        'A patched deployment disturbed another registration.'
    Assert-True (
        $patched.LayoutDirectory -eq
            (Join-Path $p.Root 'artifacts\local-package\patches\foo\x64\layout') -and
        -not (Test-Path -LiteralPath (Join-Path $p.Root 'artifacts\local-package\x64'))
    ) 'A patched deployment did not keep to its own state root.'
    [xml]$pm = Get-Content -LiteralPath (Join-Path $patched.LayoutDirectory 'AppxManifest.xml') -Raw
    $patchedAliases = @(
        $pm.SelectNodes("//*[local-name()='ExecutionAlias']") |
            ForEach-Object { $_.GetAttribute('Alias') } |
            Sort-Object
    )
    $patchedTiles = @(
        $pm.SelectNodes("//*[local-name()='VisualElements']") |
            ForEach-Object { $_.GetAttribute('DisplayName') } |
            Select-Object -Unique
    )
    Assert-True (
        $pm.Package.Identity.Name -ceq 'OpenClawFoundation.OpenClawGateway-foo' -and
        $pm.Package.Properties.DisplayName -ceq 'OpenClaw Gateway (foo)' -and
        ($patchedTiles -join ',') -ceq 'OpenClaw Gateway (foo)' -and
        ($patchedAliases -join ',') -ceq 'clawctl-foo.exe,openclaw-foo.exe'
    ) 'The patched manifest does not carry its own identity, display name, and aliases.'
    Assert-True (
        @($p.SetupPackageNames)[0] -ceq 'OpenClawFoundation.OpenClawGateway-foo' -and
        @($p.SetupPackageFamilyNames)[0] -ceq 'OpenClawFoundation.OpenClawGateway-foo_fixture'
    ) 'Setup did not target the patched package.'
    $p.Offline = $true
    Assert-True (-not (Invoke-Fixture $p @{ Patch = 'foo' }).Changed) `
        'A no-change patched re-run reported work.'

    Remove-LocalPackageRegistration -RepositoryRoot $p.Root -Patch 'foo' -Operations $p.Operations
    Assert-True (
        @($p.Removals).Count -eq 1 -and
        @($p.Removals)[0] -eq $patched.PackageFullName -and
        @($p.Installed).Count -eq 2
    ) 'Unregistering a patched identity touched another registration.'
    Assert-True (
        -not (Test-Path -LiteralPath (
            Join-Path $p.Root 'artifacts\local-package\patches\foo\x64\state.json')) -and
        (Test-Path -LiteralPath (
            Join-Path $p.Root 'artifacts\local-package\patches\foo\x64\payloads'))
    ) 'Unregistering a patched identity did not retire only its deployment state.'

    # A suffix becomes a package name, alias, and path segment, so anything
    # outside that shared alphabet fails before any state changes.
    foreach ($invalid in @('-foo', 'foo-', 'foo_bar', 'foo.bar', '..\foo', ('a' * 16))) {
        $bad = New-Fixture
        Assert-Fails { Invoke-Fixture $bad @{ Patch = $invalid } } 'Invalid -Patch'
        Assert-Fails {
            Remove-LocalPackageRegistration -RepositoryRoot $bad.Root -Patch $invalid -Operations $bad.Operations
        } 'Invalid -Patch'
        Assert-True (
            $bad.Downloads -eq 0 -and $bad.Registrations -eq 0 -and
            @($bad.PackageQueries).Count -eq 0 -and
            -not (Test-Path -LiteralPath (Join-Path $bad.Root 'artifacts'))
        ) "An invalid -Patch '$invalid' changed state before it was rejected."
    }

    # An explicitly empty -Patch, such as an unset variable, must fail rather
    # than fall back to the base identity. Falling back would let deploy with
    # -ReplaceExistingInstall remove an installed release and its app data,
    # and let -Unregister remove this checkout's base loose registration.
    foreach ($empty in @('', $null, ' ')) {
        $packaged = New-Fixture
        $packaged.Installed = @(
            [pscustomobject]@{
                Name = 'OpenClawFoundation.OpenClawGateway'
                Version = '1.2.3.4'; PackageFullName = 'OpenClawFoundation.OpenClawGateway_1.2.3.4_x64__pkg'
                PackageFamilyName = 'OpenClawFoundation.OpenClawGateway_pkg'
                InstallLocation = 'C:\Program Files\WindowsApps\fake'; IsDevelopmentMode = $false; Status = 'Ok'
            }
            [pscustomobject]@{
                Name = 'OpenClaw.Gateway'
                Version = '2026.9.403.0'; PackageFullName = 'OpenClaw.Gateway_2026.9.403.0_x64__legacy'
                PackageFamilyName = 'OpenClaw.Gateway_kaa03rpbbqef6'
                InstallLocation = 'C:\Program Files\WindowsApps\legacy'; IsDevelopmentMode = $false; Status = 'Ok'
            }
        )
        Assert-Fails {
            Invoke-Fixture $packaged @{ Patch = $empty; ReplaceExistingInstall = $true }
        } '-Patch requires a name'
        Assert-True (
            @($packaged.Removals).Count -eq 0 -and $packaged.Registrations -eq 0 -and
            @($packaged.PackageQueries).Count -eq 0 -and @($packaged.Installed).Count -eq 2
        ) "An empty -Patch '$empty' reached the base or legacy installs."

        $owned = New-Fixture
        $ownedBase = Invoke-Fixture $owned
        $queriesBefore = @($owned.PackageQueries).Count
        Assert-Fails {
            Remove-LocalPackageRegistration -RepositoryRoot $owned.Root -Patch $empty -Operations $owned.Operations
        } '-Patch requires a name'
        Assert-True (
            @($owned.Removals).Count -eq 0 -and
            @($owned.PackageQueries).Count -eq $queriesBefore -and
            @($owned.Installed)[0].PackageFullName -eq $ownedBase.PackageFullName -and
            (Test-Path -LiteralPath (Join-Path $owned.Root 'artifacts\local-package\x64\state.json'))
        ) "An empty -Patch '$empty' unregistered the base loose registration."
    }

    # An alias the patch cannot rename would collide with the base command.
    $extraAlias = New-Fixture
    $extraManifest = Join-Path $extraAlias.Root 'src\OpenClaw.Launcher\Package.appxmanifest'
    [IO.File]::WriteAllText($extraManifest, (
        (Get-Content -LiteralPath $extraManifest -Raw).Replace(
            '<uap5:ExecutionAlias Alias="clawctl.exe" />',
            '<uap5:ExecutionAlias Alias="clawctl.exe" /><uap5:ExecutionAlias Alias="claw.exe" />')))
    Assert-Fails { Invoke-Fixture $extraAlias @{ Patch = 'foo' } } 'cannot rename: claw\.exe'
    Assert-True ($extraAlias.Registrations -eq 0) 'A patched layout with an unrenamed alias was registered.'

    # An omitted -Architecture follows the device; an explicit one wins.
    $localPackageModule = Get-Module LocalPackage
    Assert-True ((& $localPackageModule {
        ConvertTo-LocalPackageArchitecture ([Runtime.InteropServices.Architecture]::X64)
    }) -ceq 'x64') 'An x64 device did not default to x64.'
    Assert-True ((& $localPackageModule {
        ConvertTo-LocalPackageArchitecture ([Runtime.InteropServices.Architecture]::Arm64)
    }) -ceq 'arm64') 'An ARM64 device did not default to arm64.'
    Assert-Fails {
        & $localPackageModule {
            ConvertTo-LocalPackageArchitecture ([Runtime.InteropServices.Architecture]::X86)
        }
    } 'Pass -Architecture x64 or -Architecture arm64'

    $native = New-Fixture
    $native.Operations.NativeArchitecture = { 'arm64' }
    $native.PreflightArchitectures = [Collections.Generic.List[string]]::new()
    $native.Operations.Preflight = {
        param($architecture)
        $native.PreflightArchitectures.Add($architecture)
        return $null
    }.GetNewClosure()
    $nativeDeployed = Invoke-Fixture $native
    $nativeLayout = Join-Path $native.Root 'artifacts\local-package\arm64\layout'
    [xml]$nativeManifest = Get-Content -LiteralPath (Join-Path $nativeLayout 'AppxManifest.xml') -Raw
    Assert-True (
        $nativeDeployed.LayoutDirectory -eq $nativeLayout -and
        $native.PreflightArchitectures.Count -eq 1 -and
        $native.PreflightArchitectures[0] -ceq 'arm64' -and
        $nativeManifest.Package.Identity.ProcessorArchitecture -eq 'arm64' -and
        (Test-Path -LiteralPath (Join-Path $nativeLayout 'mxc\arm64\wxc-exec.exe'))
    ) 'An omitted -Architecture did not deploy the native architecture.'
    Remove-LocalPackageRegistration -RepositoryRoot $native.Root -Operations $native.Operations
    Assert-True (
        $native.Removals -contains $nativeDeployed.PackageFullName -and
        -not (Test-Path -LiteralPath (Join-Path $native.Root 'artifacts\local-package\arm64\state.json'))
    ) 'An omitted -Architecture did not unregister the native deployment.'

    $explicit = New-Fixture
    $explicit.Operations.NativeArchitecture = { 'arm64' }
    $explicitDeployed = Invoke-Fixture $explicit @{ Architecture = 'x64' }
    Assert-True (
        $explicitDeployed.LayoutDirectory -eq (Join-Path $explicit.Root 'artifacts\local-package\x64\layout')
    ) 'An explicit -Architecture did not override the native default.'

    $unsupported = New-Fixture
    $unsupported.Operations.NativeArchitecture = { 'x86' }
    Assert-Fails { Invoke-Fixture $unsupported } 'unsupported value: x86'
    Assert-True ($unsupported.Registrations -eq 0) 'An unsupported native architecture was deployed.'

    # A registration of this checkout's other architecture names the fix
    # instead of reporting the checkout as a foreign owner.
    $crossArchitecture = New-Fixture
    $crossArchitectureDeployed = Invoke-Fixture $crossArchitecture @{ Architecture = 'x64' }
    $crossArchitecture.Operations.NativeArchitecture = { 'arm64' }
    Assert-Fails { Invoke-Fixture $crossArchitecture } "this checkout's x64 layout\. Run -Unregister -Architecture x64 first, then deploy again"
    Assert-Fails {
        Remove-LocalPackageRegistration -RepositoryRoot $crossArchitecture.Root -Operations $crossArchitecture.Operations
    } "this checkout's x64 layout\. Re-run -Unregister with -Architecture x64"
    Assert-True (
        $crossArchitecture.Registrations -eq 1 -and @($crossArchitecture.Removals).Count -eq 0 -and
        @($crossArchitecture.Installed)[0].PackageFullName -eq $crossArchitectureDeployed.PackageFullName
    ) 'A registration of the other architecture was touched without consent.'

    Write-Host 'Local package deployment scenarios passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
