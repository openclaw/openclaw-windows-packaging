Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:PackageName = 'OpenClaw.Gateway'
$script:StateSchema = 1
$script:ControlApplicationId = 'Control'

function Invoke-LocalPackageControlCommand {
    param(
        [Parameter(Mandatory)][string]$PackageFamilyName,
        [Parameter(Mandatory)][string]$Arguments
    )

    $package = Get-AppxPackage -Name $script:PackageName -ErrorAction Stop |
        Where-Object { $_.PackageFamilyName -eq $PackageFamilyName } |
        Select-Object -First 1
    if ($null -eq $package) {
        throw "The owning package was not registered for this user: $PackageFamilyName."
    }
    if ($package.Status.ToString() -notmatch '^(Ok|Ready)$') {
        throw "The owning package is not ready: $PackageFamilyName ($($package.Status))."
    }

    $manifestPath = Join-Path $package.InstallLocation 'AppxManifest.xml'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "The owning package manifest was not found: $manifestPath."
    }
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $application = @(
        $manifest.Package.Applications.Application |
            Where-Object { $_.Id -eq $script:ControlApplicationId }
    ) | Select-Object -First 1
    if ($null -eq $application -or [string]$application.Executable -ne 'openclaw.exe') {
        throw "The owning package does not expose the control application: $PackageFamilyName!$script:ControlApplicationId."
    }

    if (-not ('OpenClaw.PackageActivation.IApplicationActivationManager' -as [type])) {
        $source = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace OpenClaw.PackageActivation
{
    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            uint options,
            out uint processId);
    }

    [ComImport]
    [Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")]
    public class ApplicationActivationManager
    {
    }

    public static class ApplicationActivator
    {
        private const uint Synchronize = 0x00100000;
        private const uint QueryLimitedInformation = 0x1000;
        private const uint Infinite = 0xffffffff;
        private const uint WaitObject0 = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        public static int ActivateAndWait(string appUserModelId, string arguments)
        {
            var manager =
                (IApplicationActivationManager)new ApplicationActivationManager();
            int result = manager.ActivateApplication(
                appUserModelId,
                arguments,
                0,
                out uint processId);
            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            IntPtr process = OpenProcess(
                Synchronize | QueryLimitedInformation,
                false,
                processId);
            if (process == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (WaitForSingleObject(process, Infinite) != WaitObject0 ||
                    !GetExitCodeProcess(process, out uint exitCode))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return checked((int)exitCode);
            }
            finally
            {
                CloseHandle(process);
            }
        }
    }
}
'@
        Add-Type -TypeDefinition $source -Language CSharp
    }

    $exitCode = [OpenClaw.PackageActivation.ApplicationActivator]::ActivateAndWait(
        "$PackageFamilyName!$script:ControlApplicationId",
        $Arguments)
    if ($exitCode -ne 0) {
        throw "clawctl $Arguments failed (exit $exitCode)."
    }
}

function Read-LocalPackageRecord {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        $record = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    }
    catch {
        throw "Cannot read local state '$Path': $($_.Exception.Message)"
    }
    if ($record -isnot [System.Collections.IDictionary]) {
        throw "Local state must be a JSON object: $Path"
    }
    return $record
}

function Write-LocalPackageRecord {
    param([string]$Path, [System.Collections.IDictionary]$Record)

    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, ($Record | ConvertTo-Json -Depth 8) + "`n")
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Get-LocalPackageFingerprint {
    param([string[]]$Parts)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString(
            $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($Parts -join "`n")))
        ).ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Assert-LocalPackagePayload {
    param([string]$Directory, [string]$Architecture)

    $application = Join-Path $Directory 'app'
    if (-not (Test-Path -LiteralPath (Join-Path $application 'openclaw.mjs') -PathType Leaf)) {
        throw "Payload is missing app\openclaw.mjs: $Directory. Supply a complete -PayloadDirectory or use -RefreshPayload."
    }
    $metadata = Read-LocalPackageRecord (Join-Path $Directory 'payload-metadata.json')
    if ($null -eq $metadata -or $metadata['architecture'] -ne $Architecture -or
        $metadata['layout'] -ne 'expanded-directory' -or
        [string]$metadata['nodeVersion'] -notmatch '^v?\d+\.\d+\.\d+$') {
        throw "Payload metadata must describe an expanded $Architecture directory with a Node.js version: $Directory. Supply a matching payload or use -RefreshPayload."
    }
    return ([string]$metadata['nodeVersion']).TrimStart('v')
}

function Assert-LocalPackagePayloadIsSafe {
    param([string]$Directory)

    # The packaged layout links to this tree, so a link inside it would resolve
    # outside the package at run time.
    $application = Join-Path $Directory 'app'
    foreach ($entry in Get-ChildItem -LiteralPath $application -Force -Recurse) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw (
                'The downloaded payload contains a link or reparse point: ' +
                [IO.Path]::GetRelativePath($application, $entry.FullName)
            )
        }
        if (-not $entry.PSIsContainer -and
            ($entry.Name -ieq 'node.exe' -or $entry.Name -match '^node-v\d')) {
            throw (
                'The downloaded payload must not bundle Node.js: ' +
                [IO.Path]::GetRelativePath($application, $entry.FullName)
            )
        }
    }
}

function Get-LocalPackageGitHubCommand {
    # More than one gh.exe can be on PATH; take the first match rather than
    # letting .Source concatenate every candidate path.
    $gh = @(Get-Command gh -CommandType Application -ErrorAction SilentlyContinue) |
        Select-Object -First 1
    if ($null -eq $gh) {
        throw 'GitHub CLI (gh) is required to acquire a payload. Install and authenticate gh, or pass -PayloadDirectory; a completed cache needs no gh.'
    }
    return $gh
}

function Resolve-LocalPackagePayload {
    param(
        [string]$CacheDirectory,
        [string]$Architecture,
        [string]$PayloadDirectory,
        [long]$PayloadRunId,
        [switch]$RefreshPayload,
        [hashtable]$Operations
    )

    if ($PayloadDirectory) {
        $directory = (Resolve-Path -LiteralPath $PayloadDirectory -ErrorAction Stop).Path
        $nodeVersion = Assert-LocalPackagePayload $directory $Architecture
        Write-Host "Payload: supplied directly ($directory)"
        return [pscustomobject]@{
            Directory = $directory; NodeVersion = $nodeVersion
            CacheHit = $true; Superseded = $null
            PendingSelection = $null; SelectionPath = $null
        }
    }

    New-Item -Path $CacheDirectory -ItemType Directory -Force | Out-Null
    $selectionPath = Join-Path $CacheDirectory 'current.json'
    $current = $null
    try { $current = Read-LocalPackageRecord $selectionPath }
    catch {
        if (-not $RefreshPayload) {
            throw "$($_.Exception.Message) Run with -RefreshPayload to replace the invalid selection."
        }
        Write-Warning "Replacing an unreadable cache selection: $selectionPath"
    }
    if ($null -ne $current) {
        if ($current['schemaVersion'] -ne $script:StateSchema -or
            $current['architecture'] -ne $Architecture -or
            $current['generation'] -isnot [string] -or
            $current['generation'] -notmatch '^[1-9]\d*-[0-9a-f]{32}$' -or
            $current['runId'] -isnot [long] -or $current['runId'] -le 0) {
            if (-not $RefreshPayload) {
                throw "Invalid payload cache selection: $selectionPath. Run with -RefreshPayload to replace it."
            }
            Write-Warning "Replacing an invalid cache selection: $selectionPath"
            $current = $null
        }
        if ($null -ne $current -and -not $RefreshPayload -and
            ($PayloadRunId -eq 0 -or $PayloadRunId -eq $current['runId'])) {
            $directory = Join-Path $CacheDirectory $current['generation']
            $nodeVersion = Assert-LocalPackagePayload $directory $Architecture
            Write-Host "Payload: cache hit, run $($current['runId'])"
            return [pscustomobject]@{
                Directory = $directory; NodeVersion = $nodeVersion
                CacheHit = $true; Superseded = $null
                PendingSelection = $null; SelectionPath = $null
            }
        }
    }

    $runId = if ($PayloadRunId -gt 0) { $PayloadRunId } else { & $Operations.LatestRun }
    if ($runId -isnot [long] -or $runId -le 0) {
        throw 'Payload selection did not return a valid workflow run ID.'
    }
    $generation = "$runId-$([guid]::NewGuid().ToString('N'))"
    $directory = Join-Path $CacheDirectory $generation
    New-Item -Path $directory -ItemType Directory | Out-Null
    $complete = $false
    try {
        Write-Host "Payload: downloading run $runId"
        & $Operations.Download $runId $Architecture $directory | Out-Null
        $nodeVersion = Assert-LocalPackagePayload $directory $Architecture
        Assert-LocalPackagePayloadIsSafe $directory
    $complete = $true
    }
    finally {
    if (-not $complete) { Remove-Item -LiteralPath $directory -Recurse -Force }
    }

    # The selection is committed and the replaced generation retired by the
    # caller, once the whole deployment succeeds. Committing here would leave a
    # failed run selecting a payload that was never successfully deployed.
    $superseded = $null
    if ($null -ne $current) {
    $old = Join-Path $CacheDirectory $current['generation']
    if ((Test-Path -LiteralPath $old -PathType Container) -and
        (([IO.File]::GetAttributes($old) -band [IO.FileAttributes]::ReparsePoint) -eq 0)) {
        $superseded = $old
    }
    }
    return [pscustomobject]@{
    Directory = $directory; NodeVersion = $nodeVersion
    CacheHit = $false; Superseded = $superseded
    PendingSelection = [ordered]@{
        schemaVersion = $script:StateSchema
        architecture = $Architecture
        runId = $runId
        generation = $generation
    }
    SelectionPath = $selectionPath
    }
}

function Resolve-LocalPackageRuntime {
    param(
        [string]$RuntimeDirectory,
        [string]$Architecture,
        [string]$NodeVersion,
        [hashtable]$Operations
    )

    New-Item -Path $RuntimeDirectory -ItemType Directory -Force | Out-Null
    $name = "node-v$NodeVersion-win-$Architecture.zip"
    $path = Join-Path $RuntimeDirectory $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        try {
            & $Operations.TestArchive $path ([IO.Path]::GetFileNameWithoutExtension($name)) | Out-Null
            Write-Host "Node.js runtime: cached ($name)"
            return $path
        }
        catch {
            Write-Host "Node.js runtime: replacing unusable cache ($($_.Exception.Message))"
            Remove-Item -LiteralPath $path -Force
        }
    }
    Write-Host "Node.js runtime: downloading $name"
    & $Operations.DownloadRuntime $NodeVersion $name $path | Out-Null
    & $Operations.TestArchive $path ([IO.Path]::GetFileNameWithoutExtension($name)) | Out-Null
    return $path
}

function New-LocalPackageLayout {
    param(
        [string]$LayoutDirectory,
        [string]$RepositoryRoot,
        [string]$HostExecutable,
        [string]$SessionHostExecutable,
        [string]$MxcRuntimeDirectory,
        [string]$PayloadDirectory,
        [string]$RuntimeArchive,
        [string]$Architecture,
        [string]$Version
    )

    New-Item -Path $LayoutDirectory -ItemType Directory -Force | Out-Null

    $manifestPath = Join-Path $LayoutDirectory 'AppxManifest.xml'
    [xml]$manifest = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'src\OpenClaw.Launcher\Package.appxmanifest'
    ) -Raw
    $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    $identity.SetAttribute('Version', $Version)
    $identity.SetAttribute('ProcessorArchitecture', $Architecture)
    $manifest.Save($manifestPath)

    Copy-Item -LiteralPath $HostExecutable -Destination (Join-Path $LayoutDirectory 'openclaw.exe') -Force
    $images = Join-Path $LayoutDirectory 'Images'
    if (Test-Path -LiteralPath $images) { Remove-Item -LiteralPath $images -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'src\OpenClaw.Launcher\Images') `
        -Destination $images -Recurse

    # A junction keeps the expanded application out of a second copy; the
    # payload is hundreds of megabytes and never modified here.
    $applicationLink = Join-Path $LayoutDirectory 'app'
    $applicationTarget = Join-Path $PayloadDirectory 'app'
    $existing = if (Test-Path -LiteralPath $applicationLink) {
        Get-Item -LiteralPath $applicationLink -Force
    }
    else { $null }
    if ($null -ne $existing -and
        (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or
            $existing.Target -ne $applicationTarget)) {
        Remove-Item -LiteralPath $applicationLink -Recurse -Force
        $existing = $null
    }
    if ($null -eq $existing) {
        New-Item -ItemType Junction -Path $applicationLink -Target $applicationTarget | Out-Null
    }

    $runtime = Join-Path $LayoutDirectory 'runtime'
    New-Item -Path $runtime -ItemType Directory -Force | Out-Null
    foreach ($stale in Get-ChildItem -LiteralPath $runtime -File) {
        if ($stale.Name -ne [IO.Path]::GetFileName($RuntimeArchive)) {
            Remove-Item -LiteralPath $stale.FullName -Force
        }
    }
    # Replace the layout copy every time. Testing only for a file of the right
    # name would keep a truncated archive from an interrupted copy forever,
    # including under -Force, and clawctl setup would then read the bad copy.
    $runtimeTarget = Join-Path $runtime ([IO.Path]::GetFileName($RuntimeArchive))
    $runtimeStaging = "$runtimeTarget.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        Copy-Item -LiteralPath $RuntimeArchive -Destination $runtimeStaging -Force
        [IO.File]::Move($runtimeStaging, $runtimeTarget, $true)
    }
    finally {
        if (Test-Path -LiteralPath $runtimeStaging) {
            Remove-Item -LiteralPath $runtimeStaging -Force
        }
    }

    $sessionHost = Join-Path (Join-Path $LayoutDirectory 'session-host') $Architecture
    if (Test-Path -LiteralPath $sessionHost) {
        Remove-Item -LiteralPath $sessionHost -Recurse -Force
    }
    New-Item -Path $sessionHost -ItemType Directory -Force | Out-Null
    Copy-Item `
        -LiteralPath $SessionHostExecutable `
        -Destination (Join-Path $sessionHost 'openclaw-session-host.exe') `
        -Force

    $mxc = Join-Path (Join-Path $LayoutDirectory 'mxc') $Architecture
    if (Test-Path -LiteralPath $mxc) {
        Remove-Item -LiteralPath $mxc -Recurse -Force
    }
    New-Item -Path $mxc -ItemType Directory -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $MxcRuntimeDirectory -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $mxc -Recurse -Force
    }

    return $manifestPath
}

function Test-LocalPackageOwnership {
    param($Installed, [string]$LayoutDirectory)

    # An identity can only have one registration, so "ours" means the installed
    # location is this checkout's layout. Anything else belongs to another
    # checkout or tool even though the name and publisher match.
    if ($null -eq $Installed -or [string]::IsNullOrWhiteSpace($Installed.InstallLocation)) {
        return $false
    }
    return [IO.Path]::GetFullPath($Installed.InstallLocation).TrimEnd('\') -ieq
        [IO.Path]::GetFullPath($LayoutDirectory).TrimEnd('\')
}

function Test-LocalPackageLayout {
    param(
        [string]$LayoutDirectory,
        [string]$PayloadDirectory,
        [string]$RuntimeArchiveName,
        [string]$Architecture
    )

    # The registered package serves these files directly, so a short circuit
    # must confirm the live layout, not just that a previous run wrote one.
    foreach ($relative in @(
        'AppxManifest.xml',
        'openclaw.exe',
        "runtime\$RuntimeArchiveName",
        "session-host\$Architecture\openclaw-session-host.exe",
        "mxc\$Architecture\wxc-exec.exe",
        "mxc\$Architecture\plm.exe",
        "mxc\$Architecture\mxc-runtime.json"
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $LayoutDirectory $relative) -PathType Leaf)) {
            return $false
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $LayoutDirectory 'Images') -PathType Container)) {
        return $false
    }
    $link = Join-Path $LayoutDirectory 'app'
    if (-not (Test-Path -LiteralPath $link -PathType Container)) { return $false }
    $item = Get-Item -LiteralPath $link -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or
        $item.Target -ne (Join-Path $PayloadDirectory 'app')) {
        return $false
    }
    return (Test-Path -LiteralPath (Join-Path $link 'openclaw.mjs') -PathType Leaf)
}

function Get-LocalPackageOperations {
    return @{
        Now = { Get-Date }
        Preflight = {
            param($architecture)
            if (-not $IsWindows) { throw 'Registering a local package requires Windows.' }
            $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
            $unlock = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
            if ($null -eq $unlock -or
                $null -eq $unlock.PSObject.Properties['AllowDevelopmentWithoutDevLicense'] -or
                [int]$unlock.AllowDevelopmentWithoutDevLicense -ne 1) {
                throw 'Developer Mode is required to register an unpackaged layout. Enable it in Settings > System > For developers.'
            }
            $osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
            if ($architecture -eq 'arm64' -and $osArchitecture -ne 'arm64') {
                throw "An arm64 package cannot be registered on this $osArchitecture device."
            }
            if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
                throw 'The pinned .NET SDK is required. Install the SDK selected by global.json.'
            }
            $installer = Join-Path (
                [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
            ) 'Microsoft Visual Studio\Installer'
            if (-not (Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue)) {
                if (-not (Test-Path -LiteralPath (Join-Path $installer 'vswhere.exe') -PathType Leaf)) {
                    throw 'vswhere.exe was not found. Install Visual Studio Build Tools with the Desktop development with C++ workload.'
                }
                $env:Path = "$installer;$env:Path"
            }
        }
        LatestRun = {
            $gh = Get-LocalPackageGitHubCommand
            $lines = @(& $gh.Source run list --repo openclaw/openclaw-windows-packaging `
                --workflow gateway-msix.yml --branch main --status success --limit 1 --json databaseId)
            if ($LASTEXITCODE -ne 0) {
                throw "Unable to query the payload workflow (exit $LASTEXITCODE). Check gh authentication, or pass -PayloadDirectory."
            }
            $runs = @([string]::Join("`n", [string[]]$lines) | ConvertFrom-Json)
            if ($runs.Count -ne 1) {
                throw 'No successful payload workflow run was found. Pass -PayloadRunId or -PayloadDirectory.'
            }
            return [long]$runs[0].databaseId
        }
        Download = {
            param($runId, $architecture, $directory)
            $gh = Get-LocalPackageGitHubCommand
            & $gh.Source run download $runId --repo openclaw/openclaw-windows-packaging `
                --name "openclaw-gateway-payload-$architecture" --dir $directory |
                ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) {
                throw "Payload download failed (exit $LASTEXITCODE). Check gh access and artifact retention, or pass -PayloadDirectory."
            }
            return $null
        }
        DownloadRuntime = {
            param($nodeVersion, $name, $path)
            Invoke-WebRequest -Uri "https://nodejs.org/dist/v$nodeVersion/$name" -OutFile $path
            return $null
        }
        TestArchive = {
            param($path, $expectedRoot)
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive = [IO.Compression.ZipFile]::OpenRead($path)
            try {
                $entries = @($archive.Entries | Where-Object { $_.FullName -ieq "$expectedRoot/node.exe" })
                if ($entries.Count -ne 1) {
                    throw "The Node.js archive must contain exactly one '$expectedRoot/node.exe' entry."
                }
            }
            finally { $archive.Dispose() }
            return $null
        }
        StageMxcRuntime = {
            param($repositoryRoot, $architecture, $output)
            & (Join-Path $repositoryRoot 'scripts\Get-MxcRuntime.ps1') `
                -Architecture $architecture `
                -OutputDirectory $output
            return $null
        }
        Publish = {
            param($project, $architecture, $output)
            & dotnet publish $project --configuration Release --runtime "win-$architecture" `
                --self-contained "-p:Platform=$architecture" -p:PublishAot=true `
                --output $output --nologo |
                ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) {
                throw "The NativeAOT publish failed (exit $LASTEXITCODE)."
            }
            return $null
        }
        GetPackage = {
            param($name)
            $package = Get-AppxPackage -Name $name -ErrorAction Stop |
                Where-Object Name -eq $name |
                Select-Object -First 1
            if ($null -eq $package) { return $null }
            return [pscustomobject]@{
                Version = $package.Version.ToString()
                PackageFullName = $package.PackageFullName
                PackageFamilyName = $package.PackageFamilyName
                InstallLocation = $package.InstallLocation
                IsDevelopmentMode = [bool]$package.IsDevelopmentMode
                Status = $package.Status.ToString()
            }
        }
        RegisterPackage = {
            param($manifestPath)
            Add-AppxPackage -Register $manifestPath -ErrorAction Stop
            return $null
        }
        RemovePackage = {
            param($packageFullName, $preserveData)
            if ($preserveData) {
                Remove-AppxPackage -Package $packageFullName -PreserveApplicationData -ErrorAction Stop
            }
            else {
                Remove-AppxPackage -Package $packageFullName -ErrorAction Stop
            }
            return $null
        }
        TestPath = { param($path) Test-Path -LiteralPath $path }
        RunSetup = {
            param($packageFamilyName)
            Invoke-LocalPackageControlCommand -PackageFamilyName $packageFamilyName -Arguments 'setup'
            return $null
        }
    }
}

function Invoke-LocalPackagePhase {
    param([hashtable]$ProgressState, [string]$Label, [scriptblock]$Action)

    $ProgressState.Stage = $Label
    Write-Host "`n[$Label]"
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $result = & $Action
    $stopwatch.Stop()
    Write-Host ('  done in {0:0.00}s' -f $stopwatch.Elapsed.TotalSeconds)
    return $result
}

function Get-LocalPackageServices {
    param([hashtable]$Overrides)

    $services = Get-LocalPackageOperations
    foreach ($name in $Overrides.Keys) {
        if (-not $services.ContainsKey($name) -or $Overrides[$name] -isnot [scriptblock]) {
            throw "Unknown or invalid local package operation adapter: $name"
        }
        $services[$name] = $Overrides[$name]
    }
    return $services
}

function Remove-LocalPackageRegistration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
        [hashtable]$Operations = @{}
    )

    $services = Get-LocalPackageServices $Operations
    $state = Join-Path ([IO.Path]::GetFullPath($RepositoryRoot)) "artifacts\local-package\$Architecture"
    $layoutDirectory = Join-Path $state 'layout'
    $installed = & $services.GetPackage $script:PackageName
    if ($null -eq $installed) {
        Write-Host "$script:PackageName is not registered for the current user."
    }
    elseif (-not $installed.IsDevelopmentMode) {
        throw "$script:PackageName is installed from a package, not a local layout. Remove it deliberately with Remove-AppxPackage if that is what you want."
    }
    elseif (-not (Test-LocalPackageOwnership -Installed $installed -LayoutDirectory $layoutDirectory)) {
        throw (
            "$script:PackageName is registered from another location: " +
            "$($installed.InstallLocation). Run -Unregister from that checkout instead; " +
            'this one does not own that registration.'
        )
    }
    else {
        # Development-mode packages allow preserving app data, so unregistering
        # does not throw away the extracted Node.js runtime.
        & $services.RemovePackage $installed.PackageFullName $true | Out-Null
        Write-Host "Unregistered $($installed.PackageFullName); its app data was preserved."
    }
    $statePath = Join-Path $state 'state.json'
    if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -Force }
    Write-Host "Cached payload and runtime are kept under $state."
}

function Invoke-LocalPackageDeployment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
        [string]$PayloadDirectory,
        [long]$PayloadRunId,
        [switch]$RefreshPayload,
        [switch]$ReplaceExistingInstall,
        [switch]$Force,
        [switch]$SkipSetup,
        [hashtable]$Operations = @{}
    )

    if ($PayloadRunId -lt 0) { throw '-PayloadRunId must be a positive workflow run ID.' }
    if ($PayloadDirectory -and ($PayloadRunId -ne 0 -or $RefreshPayload)) {
        throw '-PayloadDirectory cannot be combined with -PayloadRunId or -RefreshPayload.'
    }

    $services = Get-LocalPackageServices $Operations
    $root = (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
    $stateRoot = Join-Path $root "artifacts\local-package\$Architecture"
    $layoutDirectory = Join-Path $stateRoot 'layout'
    $statePath = Join-Path $stateRoot 'state.json'
    $progress = @{ Stage = 'preparing' }
    $total = [Diagnostics.Stopwatch]::StartNew()

    try {
        & $services.Preflight $Architecture | Out-Null
        New-Item -Path $stateRoot -ItemType Directory -Force | Out-Null
        Write-Host "Registering a local development build of $script:PackageName ($Architecture)."
        Write-Host "Layout: $layoutDirectory"

        $installed = Invoke-LocalPackagePhase $progress 'Check current registration' {
            & $services.GetPackage $script:PackageName
        }
        if ($null -ne $installed -and -not $installed.IsDevelopmentMode) {
            if (-not $ReplaceExistingInstall) {
                throw (
                    "$script:PackageName is already installed from a package (version $($installed.Version)). " +
                    'Windows cannot replace a packaged install with a local layout. ' +
                    'Re-run with -ReplaceExistingInstall to remove it first; its packaged app data ' +
                    'cannot be preserved across that switch.'
                )
            }
            Write-Warning "Removing the packaged $script:PackageName $($installed.Version); its app data cannot be preserved."
            & $services.RemovePackage $installed.PackageFullName $false | Out-Null
            $installed = $null
        }
        elseif ($null -ne $installed -and
            -not (Test-LocalPackageOwnership -Installed $installed -LayoutDirectory $layoutDirectory)) {
            # A development registration from another checkout or tool. Only one
            # registration of this identity can exist, but it is not ours to take.
            if (-not $ReplaceExistingInstall) {
                throw (
                    "$script:PackageName is already registered from another location: " +
                    "$($installed.InstallLocation). Run -Unregister from that checkout, or " +
                    're-run with -ReplaceExistingInstall to take over the identity here.'
                )
            }
            Write-Warning "Taking over the registration at $($installed.InstallLocation)."
        }

        $payload = Invoke-LocalPackagePhase $progress 'Resolve payload' {
            Resolve-LocalPackagePayload -CacheDirectory (Join-Path $stateRoot 'payloads') `
                -Architecture $Architecture -PayloadDirectory $PayloadDirectory `
                -PayloadRunId $PayloadRunId -RefreshPayload:$RefreshPayload -Operations $services
        }
        $runtimeArchive = Invoke-LocalPackagePhase $progress 'Resolve Node.js runtime' {
            Resolve-LocalPackageRuntime -RuntimeDirectory (Join-Path $stateRoot 'runtime') `
                -Architecture $Architecture -NodeVersion $payload.NodeVersion -Operations $services
        }
        $mxcRuntimeDirectory = Join-Path $stateRoot 'mxc'
        Invoke-LocalPackagePhase $progress 'Stage MXC runtime' {
            & $services.StageMxcRuntime $root $Architecture $mxcRuntimeDirectory
        } | Out-Null
        $hostDirectory = Join-Path $stateRoot 'host'
        Invoke-LocalPackagePhase $progress 'Build launcher (NativeAOT)' {
            & $services.Publish (Join-Path $root 'src\OpenClaw.Launcher\OpenClaw.Launcher.csproj') `
                $Architecture $hostDirectory
        } | Out-Null
        $sessionHostDirectory = Join-Path $stateRoot 'session-host'
        Invoke-LocalPackagePhase $progress 'Build session host (NativeAOT)' {
            & $services.Publish (Join-Path $root 'src\OpenClaw.SessionHost\OpenClaw.SessionHost.csproj') `
                $Architecture $sessionHostDirectory
        } | Out-Null
        $hostExecutable = Join-Path $hostDirectory 'openclaw.exe'
        if (-not (& $services.TestPath $hostExecutable)) {
            throw "The publish did not produce $hostExecutable."
        }
        $sessionHostExecutable = Join-Path $sessionHostDirectory 'openclaw-session-host.exe'
        if (-not (& $services.TestPath $sessionHostExecutable)) {
            throw "The publish did not produce $sessionHostExecutable."
        }

        $hostInfo = Get-Item -LiteralPath $hostExecutable
        $sessionHostInfo = Get-Item -LiteralPath $sessionHostExecutable
        $manifestSource = Join-Path $root 'src\OpenClaw.Launcher\Package.appxmanifest'
        # Hash the launcher rather than trusting its timestamp: publish copies
        # into the output directory and can refresh timestamps with no source
        # change, which would defeat the up-to-date check on every run.
        $hostHash = (Get-FileHash -LiteralPath $hostExecutable -Algorithm SHA256).Hash
        $sessionHostHash = (
            Get-FileHash -LiteralPath $sessionHostExecutable -Algorithm SHA256
        ).Hash
        $mxcHashes = @(
            Get-ChildItem -LiteralPath $mxcRuntimeDirectory -File -Recurse |
                Sort-Object FullName |
                ForEach-Object {
                    "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
                }
        )
        $imageHashes = @(
            Get-ChildItem -LiteralPath (Join-Path $root 'src\OpenClaw.Launcher\Images') -File -Recurse |
                Sort-Object FullName |
                ForEach-Object { "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
        )
        $fingerprint = Get-LocalPackageFingerprint (@(
            $Architecture
            $payload.Directory
            [IO.Path]::GetFileName($runtimeArchive)
            $hostInfo.Length.ToString()
            $hostHash
            $sessionHostInfo.Length.ToString()
            $sessionHostHash
            (Get-FileHash -LiteralPath $manifestSource -Algorithm SHA256).Hash
        ) + $imageHashes + $mxcHashes)
        $previous = Read-LocalPackageRecord $statePath
        $setupSatisfied = $SkipSetup -or ($null -ne $previous -and $previous['setupComplete'] -eq $true)
        if (-not $Force -and $null -ne $previous -and $null -ne $installed -and
            $installed.IsDevelopmentMode -and
            (Test-LocalPackageOwnership -Installed $installed -LayoutDirectory $layoutDirectory) -and
            $previous['fingerprint'] -eq $fingerprint -and
            $previous['version'] -eq $installed.Version -and
            $installed.Status -ieq 'Ok' -and
            $setupSatisfied -and
            (Test-LocalPackageLayout -LayoutDirectory $layoutDirectory `
                -PayloadDirectory $payload.Directory `
                -RuntimeArchiveName ([IO.Path]::GetFileName($runtimeArchive)) `
                -Architecture $Architecture)) {
            $total.Stop()
            Write-Host "`nAlready up to date: $($installed.PackageFullName)"
            Write-Host ('Total {0:0.00}s. Use -Force to re-register anyway.' -f $total.Elapsed.TotalSeconds)
            return [pscustomobject]@{
                Version = $installed.Version
                PackageFullName = $installed.PackageFullName
                LayoutDirectory = $layoutDirectory
                Changed = $false
            }
        }

        $version = Get-LocalPackageNextVersion `
            -InstalledVersion $(if ($null -ne $installed) { $installed.Version } else { '' }) `
            -PreviousVersion $(if ($null -ne $previous) { [string]$previous['version'] } else { '' }) `
            -Now (& $services.Now)

        $manifestPath = Invoke-LocalPackagePhase $progress 'Assemble layout' {
            New-LocalPackageLayout -LayoutDirectory $layoutDirectory -RepositoryRoot $root `
                -HostExecutable $hostExecutable -SessionHostExecutable $sessionHostExecutable `
                -MxcRuntimeDirectory $mxcRuntimeDirectory -PayloadDirectory $payload.Directory `
                -RuntimeArchive $runtimeArchive -Architecture $Architecture -Version $version
        }
        Invoke-LocalPackagePhase $progress 'Register package' {
            # Re-registering over an existing development registration does not
            # reliably repair one whose layout was deleted or damaged: the
            # package still reports Status Ok while its aliases fail with "The
            # process has no package identity". Removing first, preserving app
            # data, makes registration deterministic and self-healing.
            if ($null -ne $installed -and $installed.IsDevelopmentMode) {
                & $services.RemovePackage $installed.PackageFullName $true | Out-Null
            }
            & $services.RegisterPackage $manifestPath
        } | Out-Null

        $registered = & $services.GetPackage $script:PackageName
        if ($null -eq $registered -or $registered.Version -ne $version -or
            -not $registered.IsDevelopmentMode -or $registered.Status -inotin @('Ok', 'Ready')) {
            throw 'The package did not register as a healthy local development build.'
        }
        Write-LocalPackageRecord $statePath ([ordered]@{
            schemaVersion = $script:StateSchema
            version = $version
            fingerprint = $fingerprint
            setupComplete = $false
            packageFullName = $registered.PackageFullName
            layoutDirectory = $layoutDirectory
            payloadDirectory = $payload.Directory
        })

        if (-not $SkipSetup) {
            Invoke-LocalPackagePhase $progress 'Prepare bundled Node.js runtime' {
                & $services.RunSetup $registered.PackageFamilyName
            } | Out-Null
        }

        # Record completion only once the package is actually runnable, so a
        # failed or skipped setup cannot be short-circuited as up to date.
        Write-LocalPackageRecord $statePath ([ordered]@{
            schemaVersion = $script:StateSchema
            version = $version
            fingerprint = $fingerprint
            setupComplete = (-not $SkipSetup)
            packageFullName = $registered.PackageFullName
            layoutDirectory = $layoutDirectory
            payloadDirectory = $payload.Directory
        })
        if ($null -ne $payload.PendingSelection) {
            Write-LocalPackageRecord $payload.SelectionPath $payload.PendingSelection
        }
        if ($payload.Superseded) {
            Remove-Item -LiteralPath $payload.Superseded -Recurse -Force -ErrorAction SilentlyContinue
        }

        $total.Stop()
        Write-Host "`nRegistered: $($registered.PackageFullName)"
        Write-Host $(if ($SkipSetup) {
            'Run `clawctl setup` once, then `openclaw`.'
        }
        else { 'Ready to run: `openclaw`' })
        Write-Host ('Total {0:0.00}s.' -f $total.Elapsed.TotalSeconds)
        return [pscustomobject]@{
            Version = $version
            PackageFullName = $registered.PackageFullName
            LayoutDirectory = $layoutDirectory
            Changed = $true
        }
    }
    catch {
        $total.Stop()
        Write-Host "`nFAILED during $($progress.Stage): $($_.Exception.Message)"
        throw
    }
}

function Get-LocalPackageNextVersion {
    param(
        [string]$InstalledVersion,
        [string]$PreviousVersion,
        [datetime]$Now = (Get-Date)
    )

    $parse = {
        param($value)
        if (-not $value) { return [version]'0.0.0.0' }
        $parsed = $null
        if ($value -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$' -or
            -not [version]::TryParse($value, [ref]$parsed) -or
            @($parsed.Major, $parsed.Minor, $parsed.Build, $parsed.Revision).
                Where({ $_ -gt [uint16]::MaxValue }).Count -ne 0) {
            throw "Invalid MSIX version '$value'. Use four numeric components from 0 through 65535."
        }
        return $parsed
    }
    $highest = & $parse $InstalledVersion
    $previous = & $parse $PreviousVersion
    if ($previous -gt $highest) { $highest = $previous }

    $days = [int]($Now.Date - [datetime]'2020-01-01').TotalDays
    if ($days -lt 0 -or $days -gt [uint16]::MaxValue) {
        throw 'The current date cannot be encoded as a local package version.'
    }
    $candidate = [version]"0.1.$days.$([int]$Now.TimeOfDay.TotalSeconds % 65536)"
    if ($candidate -gt $highest) { return $candidate.ToString(4) }

    $parts = @($highest.Major, $highest.Minor, $highest.Build, $highest.Revision)
    for ($index = 3; $index -ge 0; $index--) {
        if ($parts[$index] -lt [uint16]::MaxValue) {
            $parts[$index]++
            return $parts -join '.'
        }
        $parts[$index] = 0
    }
    throw 'The local package version space is exhausted.'
}

Export-ModuleMember -Function Invoke-LocalPackageDeployment, Remove-LocalPackageRegistration,
    Get-LocalPackageNextVersion
