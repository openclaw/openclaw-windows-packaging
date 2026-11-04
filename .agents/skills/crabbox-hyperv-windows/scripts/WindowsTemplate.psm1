. (Join-Path $PSScriptRoot 'WindowsTemplate.Helpers.ps1')

function Initialize-CrabboxWindowsTemplate {
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$VhdxPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$CrabboxPath,
    [Parameter(Mandatory)][string]$Switch,
    [string]$AdminUser = 'crabbox',
    [Security.SecureString]$AdminPassword,
    [switch]$Prime,
    [ValidateRange(1,60)][int]$PrimeTimeoutMinutes = 25
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = Get-Item -LiteralPath $VhdxPath
if ($source.PSIsContainer -or $source.Extension -ine '.vhdx' -or $source.FullName.StartsWith('\\') -or
    $source.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'VhdxPath must be an existing local VHDX file.'
}
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if ($destination.StartsWith('\\') -or (Test-Path -LiteralPath $destination) -or
    -not (Test-Path -LiteralPath (Split-Path -Parent $destination) -PathType Container)) {
    throw 'OutputDirectory must be a new local directory under an existing parent.'
}
if ((Get-Item -LiteralPath (Split-Path -Parent $destination)).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'Use an output parent that is not a reparse point.'
}
$binary = Get-Item -LiteralPath $CrabboxPath
if ($binary.PSIsContainer -or $binary.Extension -ine '.exe') { throw 'CrabboxPath must name the verified executable.' }
if ($AdminUser -notmatch '^[A-Za-z][A-Za-z0-9_.-]{0,19}$' -or
    $AdminUser -in @('Administrator','Guest','DefaultAccount','WDAGUtilityAccount')) { throw 'Use a new valid local administrator name.' }
if (-not $PSCmdlet.ShouldProcess($destination, 'Create a private VHDX copy and inject a local administrator; optionally boot through OOBE')) {
    return
}
if (-not $AdminPassword) { throw 'Supply -AdminPassword from Read-Host -AsSecureString; passwords are never generated or printed.' }
if ($AdminPassword.Length -lt 14) { throw 'Use a strong password of at least 14 characters.' }
if (-not (Test-WindowsTemplateAdministrator)) {
    throw 'Open PowerShell 7 with Run as administrator for offline VHDX mounting.'
}
foreach ($command in @('Get-VHD','Mount-VHD','Dismount-VHD','Get-Disk','Get-Partition','Get-Volume','Get-VMSwitch')) {
    Get-Command $command -ErrorAction Stop | Out-Null
}
$binaryInfo = Get-CrabboxTemplateExecutable -Path $binary.FullName
Get-VMSwitch -Name $Switch -ErrorAction Stop | Out-Null
$original = Get-VHD -Path $source.FullName
if ($original.Attached -or $original.ParentPath -or $original.VhdFormat -ne 'VHDX') {
    throw 'Use a detached standalone VHDX, not an attached or differencing disk.'
}
if (@(Get-VM | Get-VMHardDiskDrive | Where-Object Path -EQ $source.FullName).Count) {
    throw 'Source VHDX is attached to a registered VM; make a separate offline copy first.'
}
$sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
$output = Join-Path $destination 'windows-template.vhdx'
$mountedHere = $false
$vmId = $null
$complete = $false
$operationFailure = $null
$cleanupFailures = [Collections.Generic.List[Exception]]::new()
try {
    New-CrabboxPrivateDirectory -Path $destination -Confirm:$false
    [IO.File]::Copy($source.FullName, $output, $false)
    Protect-CrabboxTemplateFile -Path $output -Confirm:$false
    $mounted = Mount-VHD -Path $output -PassThru
    $mountedHere = $true
    $disk = Get-Disk -Number $mounted.DiskNumber
    $owned = Get-VHD -Path $output
    if (-not $owned.Attached -or $owned.DiskNumber -ne $disk.Number -or
        $disk.PartitionStyle -ne 'GPT' -or $disk.IsBoot -or $disk.IsSystem) {
        throw 'Copied disk identity/GPT verification failed.'
    }
    $partitions = @($disk | Get-Partition)
    if (@($partitions | Where-Object { [guid]$_.GptType -eq [guid]'c12a7328-f81f-11d2-ba4b-00a0c93ec93b' }).Count -ne 1) {
        throw 'Expected one EFI system partition for a Gen2 template.'
    }
    $volumes = @($partitions | Where-Object DriveLetter | Get-Volume | Where-Object {
        $_.DriveLetter -and (Test-Path -LiteralPath "$($_.DriveLetter):\Windows\System32\Config\SYSTEM")
    })
    if ($volumes.Count -ne 1) { throw 'Expected exactly one Windows volume on the owned copy.' }
    $windowsRoot = "$($volumes[0].DriveLetter):\Windows"
    $architecture = Get-CrabboxWindowsArchitecture -WindowsRoot $windowsRoot
    $hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    if (($architecture -eq 'amd64' -and $hostArchitecture -ne 'X64') -or
        ($architecture -eq 'arm64' -and $hostArchitecture -ne 'Arm64')) { throw 'Guest architecture must match the Hyper-V host.' }
    $answer = Join-Path $windowsRoot 'Panther\unattend.xml'
    if (Test-Path -LiteralPath $answer) { throw 'The image already contains an answer file; refusing to replace it.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $answer) -Force | Out-Null
    $xml = ConvertTo-CrabboxUnattendXml -AdminUser $AdminUser -AdminPassword $AdminPassword -Architecture $architecture
    Write-CrabboxTemplateAnswer -Path $answer -Xml $xml
    Dismount-VHD -Path $output
    $mountedHere = $false
    if ($Prime) {
        $vmName = "crabbox-prime-$([guid]::NewGuid().ToString('N'))"
        $vm = New-VM -Name $vmName -Generation 2 -MemoryStartupBytes 4GB -VHDPath $output -SwitchName $Switch
        $vmId = $vm.Id
        Set-VM -VM $vm -ProcessorCount 2 -AutomaticCheckpointsEnabled $false -AutomaticStopAction TurnOff
        Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
        Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
        Enable-VMTPM -VM $vm
        Start-VM -VM $vm
        $credential = [pscredential]::new($AdminUser, $AdminPassword)
        $deadline = [DateTimeOffset]::UtcNow.AddMinutes($PrimeTimeoutMinutes)
        $ready = $false
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $probe = Invoke-CrabboxPrimeProbe -VmId $vmId -Credential $credential
            if ($probe -and $probe.ImageState -eq 'IMAGE_STATE_COMPLETE' -and $probe.Administrator) {
                $ready = $true
                break
            }
            Start-Sleep -Seconds 5
        }
        if (-not $ready) { throw 'OOBE did not reach IMAGE_STATE_COMPLETE with the selected administrator; retained output is not qualified.' }
        Stop-VM -VM $vm -Confirm:$false
        $shutdown = [DateTimeOffset]::UtcNow.AddMinutes(3)
        while ((Get-VM -Id $vmId).State -ne 'Off' -and [DateTimeOffset]::UtcNow -lt $shutdown) { Start-Sleep -Seconds 2 }
        if ((Get-VM -Id $vmId).State -ne 'Off') { throw 'Graceful shutdown did not finish; template is not qualified.' }
    }
    $complete = $true
} catch {
    $operationFailure = $_
} finally {
    try {
        if ($vmId) {
            $ownedVm = Get-VM -Id $vmId -ErrorAction Stop
            if ($ownedVm.State -ne 'Off') {
                Write-Warning 'Priming incomplete; turning off only the task-owned VM. Retained image is not qualified.'
                Stop-VM -VM $ownedVm -TurnOff -Force
            }
            Remove-VM -VM $ownedVm -Force
        }
    } catch {
        $cleanupFailures.Add($_.Exception)
    } finally {
        if ($mountedHere) {
            try { Dismount-VHD -Path $output } catch { $cleanupFailures.Add($_.Exception) }
        }
        if (-not $complete -and (Test-Path -LiteralPath $output)) {
            Write-Warning "Incomplete private template retained at $output; it may contain credential material. Do not share or reuse it as qualified."
        }
    }
    if ($operationFailure -and $cleanupFailures.Count -eq 0) { throw $operationFailure }
    if ($cleanupFailures.Count) {
        if ($operationFailure) { $cleanupFailures.Insert(0,$operationFailure.Exception) }
        throw [AggregateException]::new('Template preparation or exact owned-resource cleanup failed; inspect inner errors and private output.', $cleanupFailures)
    }
}
if ((Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'Source VHDX changed during preparation; inspect it before any reuse.'
}
$final = Get-VHD -Path $output
if ($final.Attached -or $final.ParentPath) { throw 'Output is not a detached standalone template.' }
[pscustomobject]@{
    VhdxPath = $output; AdminUser = $AdminUser; Switch = $Switch
    CrabboxPath = $binary.FullName; CrabboxVersion = $binaryInfo.Version
    CrabboxSha256 = $binaryInfo.Sha256
    Verification = if ($Prime) { 'IMAGE_STATE_COMPLETE, administrator login and graceful shutdown verified' }
        else { 'Answer file injected only; not boot-tested or qualified for Crabbox' }
    CredentialWarning = 'Private image may retain reversible Setup credentials; keep it private and retain the password securely.'
}
}

function Test-WindowsTemplateAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-CrabboxTemplateExecutable {
    param([Parameter(Mandatory)][string]$Path)
    $version = @(& $Path --version)
    if ($LASTEXITCODE -ne 0) { throw 'The selected Crabbox binary failed --version.' }
    $providers = @(& $Path providers --json)
    if ($LASTEXITCODE -ne 0 -or [string]::Join("`n", [string[]]$providers) -notmatch '"hyperv"') {
        throw 'The selected Crabbox executable does not advertise the hyperv provider.'
    }
    [pscustomobject]@{ Version = [string]::Join(' ', [string[]]$version); Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}

function Write-CrabboxTemplateAnswer {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Xml)
    # The mounted volume is a separate access path; its parent directory's host ACL does not protect it.
    $stream = [IO.FileSystemAclExtensions]::Create([IO.FileInfo]::new($Path),
        [IO.FileMode]::CreateNew, [Security.AccessControl.FileSystemRights]::FullControl,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::None, (Get-CrabboxTemplateFileSecurity))
    try {
        Assert-CrabboxTemplatePrivacy -Security ([IO.FileSystemAclExtensions]::GetAccessControl($stream))
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Xml)
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}

Export-ModuleMember -Function Initialize-CrabboxWindowsTemplate
