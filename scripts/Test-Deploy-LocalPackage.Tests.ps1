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

function New-Fixture {
    $root = Join-Path $testRoot "repo with spaces $([guid]::NewGuid().ToString('N'))"
    $project = Join-Path $root 'src\OpenClaw.Launcher'
    New-Item -Path (Join-Path $project 'Images') -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $project 'Images\StoreLogo.png'), 'fixture image')
    [IO.File]::WriteAllText((Join-Path $project 'Package.appxmanifest'), @'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="OpenClaw.Gateway" Publisher="CN=Fixture" Version="0.0.0.0" />
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
        Downloads = 0
        RuntimeDownloads = 0
        Publishes = 0
        Registrations = 0
        Setups = 0
        SetupFailure = $false
        Removals = @()
        PreserveFlags = @()
    }
    $state.WritePayload = {
        param($directory, $architecture, $text, $nodeVersion)
        New-Item -Path (Join-Path $directory 'app') -ItemType Directory -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $directory 'app\openclaw.mjs'), $text)
        [IO.File]::WriteAllText((Join-Path $directory 'payload-metadata.json'), (@{
            architecture = $architecture; layout = 'expanded-directory'; nodeVersion = $nodeVersion
        } | ConvertTo-Json))
    }
    $state.Operations = @{
        Now = { $state.Now }.GetNewClosure()
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
        Publish = {
            param($project, $architecture, $output)
            $state.Publishes++
            if ($state.PublishFailure) { throw 'NativeAOT publish failed.' }
            New-Item -Path $output -ItemType Directory -Force | Out-Null
            if (-not $state.SkipHost) {
                # Unchanged source must produce identical bytes, as a real
                # incremental publish does; HostVersion models a source edit.
                [IO.File]::WriteAllText((Join-Path $output 'openclaw.exe'), "host $($state.HostVersion)")
            }
            return $null
        }.GetNewClosure()
        GetPackage = { param($name) $state.Installed }.GetNewClosure()
        RegisterPackage = {
            param($manifestPath)
            $state.Registrations++
            if ($state.RegisterFailure) { throw '0x80073CFB: registration blocked.' }
            [xml]$m = Get-Content -LiteralPath $manifestPath -Raw
            $state.Installed = [pscustomobject]@{
                Version = if ($state.BadRegistration) { '9.9.9.9' } else { $m.Package.Identity.Version }
                PackageFullName = "OpenClaw.Gateway_$($m.Package.Identity.Version)_fixture"
                InstallLocation = Split-Path $manifestPath -Parent
                IsDevelopmentMode = $true
                Status = 'Ok'
            }
            return $null
        }.GetNewClosure()
        RemovePackage = {
            param($packageFullName, $preserveData)
            $state.Removals += $packageFullName
            $state.PreserveFlags += [bool]$preserveData
            $state.Installed = $null
            return $null
        }.GetNewClosure()
        TestPath = { param($path) Test-Path -LiteralPath $path }
        RunSetup = {
            $state.Setups++
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
        $f.Publishes -eq 1 -and $f.Registrations -eq 1) 'First deployment did not acquire and register exactly once.'
    Assert-True ($f.Setups -eq 1) 'Deployment did not leave the package runnable by preparing the runtime.'
    Assert-True (@($first).Count -eq 1 -and $first.PackageFullName) 'Deployment did not return a single registration record.'
    $layout = $first.LayoutDirectory
    Assert-True ((Get-Content (Join-Path $layout 'app\openclaw.mjs') -Raw) -eq 'first payload') 'Layout does not expose the payload application.'
    Assert-True ((Get-Item (Join-Path $layout 'app')).Attributes -band [IO.FileAttributes]::ReparsePoint) 'The layout copied the application instead of linking it.'
    Assert-True (Test-Path (Join-Path $layout 'openclaw.exe')) 'Layout is missing the launcher.'
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

    # A source change must reach the registered package.
    $f.Now = $f.Now.AddMinutes(5)
    $f.HostVersion = 2
    $changed = Invoke-Fixture $f
    Assert-True ($changed.Changed) 'A launcher change was not detected.'
    Assert-True ((Get-Content (Join-Path $layout 'openclaw.exe') -Raw) -eq 'host 2') 'The layout kept a stale launcher.'

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
        Version = '1.2.3.4'; PackageFullName = 'OpenClaw.Gateway_1.2.3.4_x64__pkg'
        InstallLocation = 'C:\Program Files\WindowsApps\fake'; IsDevelopmentMode = $false; Status = 'Ok'
    }
    Assert-Fails { Invoke-Fixture $g } 'already installed from a package'
    Assert-True ($g.Registrations -eq 0 -and @($g.Removals).Count -eq 0) 'A conflicting packaged install was touched without consent.'
    $replaced = Invoke-Fixture $g @{ ReplaceExistingInstall = $true }
    Assert-True ($g.Removals -contains 'OpenClaw.Gateway_1.2.3.4_x64__pkg' -and $replaced.Changed) 'Explicit replacement did not remove the packaged install.'
    # Removal happens first, so the dev build need not out-version the package it
    # replaced; staying on 0.1.x keeps a later real release installable.
    Assert-True ([version]$replaced.Version -lt [version]'1.0.0.0') 'A replacement build should not claim a release-range version.'

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

    # Argument guards.
    $j = New-Fixture
    $external = Join-Path $testRoot 'supplied payload with spaces'
    & $j.WritePayload $external 'x64' 'supplied' '24.20.0'
    $supplied = Invoke-Fixture $j @{ PayloadDirectory = $external }
    Assert-True ($j.Downloads -eq 0 -and $j.Queries -eq 0) 'A supplied payload still contacted GitHub.'
    Assert-True ((Get-Content (Join-Path $supplied.LayoutDirectory 'app\openclaw.mjs') -Raw) -eq 'supplied') 'A supplied payload was not used.'
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
        Version = '1.0.0.0'; PackageFullName = 'pkg'; InstallLocation = 'x'
        IsDevelopmentMode = $false; Status = 'Ok'
    }
    Assert-Fails { Remove-LocalPackageRegistration -RepositoryRoot $k.Root -Operations $k.Operations } 'not a local layout'

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
    foreach ($break in @('openclaw.exe', 'Images', 'app', 'runtime')) {
        $d = New-Fixture
        $deployed = Invoke-Fixture $d
        $target = Join-Path $deployed.LayoutDirectory $break
        Remove-Item -LiteralPath $target -Recurse -Force
        $repaired = Invoke-Fixture $d
        Assert-True ($repaired.Changed) "A layout missing '$break' was reported as up to date."
        Assert-True (Test-Path -LiteralPath $target) "A layout missing '$break' was not repaired."
    }

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
        Version = '0.1.0.0'; PackageFullName = 'OpenClaw.Gateway_0.1.0.0_x64__other'
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
        Version = $mine.Version
        PackageFullName = "OpenClaw.Gateway_$($mine.Version)_x64__other"
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

    Write-Host 'Local package deployment scenarios passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
