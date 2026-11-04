. (Join-Path $PSScriptRoot 'WindowsIsoVhdx.Helpers.ps1')

function Invoke-WindowsIsoVhdx {
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][string]$IsoPath,
    [Parameter(Mandatory)][string]$VhdxPath,
    [ValidateRange(1, 65535)][int]$ImageIndex,
    [string]$ImageName,
    [ValidateRange(64GB, 64TB)][long]$SizeBytes = 80GB
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSBoundParameters.ContainsKey('ImageIndex') -and $ImageName) {
    throw 'Specify -ImageIndex or -ImageName, not both.'
}
$iso = Get-Item -LiteralPath $IsoPath
if ($iso.PSIsContainer -or $iso.Extension -ine '.iso' -or $iso.PSProvider.Name -ne 'FileSystem') {
    throw 'IsoPath must name an existing local Windows installation ISO.'
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($VhdxPath)
if (Test-Path -LiteralPath $output -PathType Container) { $output = Join-Path $output "$($iso.BaseName).vhdx" }
if ([IO.Path]::GetExtension($output) -ine '.vhdx' -or $output.StartsWith('\\')) {
    throw 'VhdxPath must be a local .vhdx path or an existing directory.'
}
if (Test-Path -LiteralPath $output) { throw 'Refusing to overwrite an existing output.' }
$parent = Get-Item -LiteralPath (Split-Path -Parent $output)
if (-not $parent.PSIsContainer -or $parent.PSProvider.Name -ne 'FileSystem') {
    throw 'The output directory must already exist on a local filesystem.'
}
if (-not $PSCmdlet.ShouldProcess($output, "Create a new $SizeBytes-byte dynamic GPT/UEFI Windows VHDX from $($iso.FullName)")) {
    return
}
if (-not (Test-WindowsImageAdministrator)) {
    throw 'Open PowerShell 7 with Run as administrator, then run this script again.'
}
foreach ($command in @('New-VHD','Mount-VHD','Dismount-VHD','Get-VHD','Mount-DiskImage',
    'Get-DiskImage','Dismount-DiskImage','Get-Volume','Get-Disk','Initialize-Disk',
    'New-Partition','Remove-Partition','Format-Volume','Get-Partition')) {
    Get-Command $command -ErrorAction Stop | Out-Null
}
$dism = Join-Path $env:SystemRoot 'System32\dism.exe'
$bcdboot = Join-Path $env:SystemRoot 'System32\bcdboot.exe'
foreach ($tool in @($dism, $bcdboot)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Required deployment tool is missing: $tool" }
}
$mountedIsoHere = $false
$mountedVhdHere = $false
$completed = $false
$operationFailure = $null
$cleanupFailures = [Collections.Generic.List[Exception]]::new()
try {
    $media = Get-DiskImage -ImagePath $iso.FullName
    if (-not $media.Attached) {
        $media = Mount-DiskImage -ImagePath $iso.FullName -PassThru
        $mountedIsoHere = $true
    }
    $volumes = @($media | Get-Volume | Where-Object DriveLetter)
    if ($volumes.Count -ne 1) { throw 'Expected exactly one drive-letter volume on the ISO.' }
    $mediaRoot = "$($volumes[0].DriveLetter):\"
    $images = @('sources\install.wim','sources\install.esd' | ForEach-Object { Join-Path $mediaRoot $_ } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($images.Count -ne 1) { throw 'Expected one install.wim or install.esd; split SWM is unsupported.' }
    $image = $images[0]
    $listing = @(Invoke-WindowsImageTool -Path $dism -Arguments @('/English','/Get-WimInfo',"/WimFile:$image"))
    $selected = Select-WindowsIsoImage -Images @(ConvertFrom-DismWimInfo -Lines $listing) `
        -ImageIndex $ImageIndex -ImageName $ImageName
    $detail = @(Invoke-WindowsImageTool -Path $dism -Arguments @('/English','/Get-WimInfo',"/WimFile:$image","/Index:$($selected.Index)"))
    $selected = @(ConvertFrom-DismWimInfo -Lines $detail)[0]
    if (-not $selected -or $selected.Architecture -notin @('x64','arm64')) {
        throw 'Expected x64 or ARM64 Windows image metadata.'
    }
    New-VHD -Path $output -Dynamic -SizeBytes $SizeBytes -LogicalSectorSizeBytes 512 | Out-Null
    $vhd = Mount-VHD -Path $output -PassThru
    $mountedVhdHere = $true
    $disk = Get-Disk -Number $vhd.DiskNumber
    $attached = Get-VHD -Path $output
    if (-not $attached.Attached -or $attached.DiskNumber -ne $disk.Number -or
        $disk.PartitionStyle -ne 'RAW' -or $disk.Size -ne $SizeBytes -or $disk.IsBoot -or $disk.IsSystem) {
        throw 'New VHDX identity/RAW-state verification failed; refusing to initialize.'
    }
    $layout = Initialize-WindowsIsoVhdxLayout -DiskNumber $disk.Number -Confirm:$false
    $efiRoot = "$($layout.Efi.DriveLetter):\"
    $windowsRoot = "$($layout.Windows.DriveLetter):\"
    Invoke-WindowsImageTool -Path $dism -Arguments @('/English','/Quiet','/Apply-Image',"/ImageFile:$image","/Index:$($selected.Index)","/ApplyDir:$windowsRoot",'/CheckIntegrity') | Out-Host
    Invoke-WindowsImageTool -Path $bcdboot -Arguments @((Join-Path $windowsRoot 'Windows'),'/s',"$($layout.Efi.DriveLetter):",'/f','UEFI') | Out-Host
    $fallback = if ($selected.Architecture -eq 'arm64') { 'bootaa64.efi' } else { 'bootx64.efi' }
    foreach ($file in @((Join-Path $windowsRoot 'Windows\System32\Config\SYSTEM'),
        (Join-Path $windowsRoot 'Windows\System32\winload.efi'),
        (Join-Path $efiRoot 'EFI\Microsoft\Boot\BCD'), (Join-Path $efiRoot "EFI\Boot\$fallback"))) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Offline verification missing: $file" }
    }
    Assert-WindowsIsoVhdxLayout -Partitions @(Get-Partition -DiskNumber $disk.Number)
    $completed = $true
} catch {
    $operationFailure = $_
} finally {
    if ($mountedVhdHere) {
        try { Dismount-VHD -Path $output } catch { $cleanupFailures.Add($_.Exception) }
    }
    if ($mountedIsoHere) {
        try { Dismount-DiskImage -ImagePath $iso.FullName | Out-Null } catch { $cleanupFailures.Add($_.Exception) }
    }
    if (-not $completed -and (Test-Path -LiteralPath $output)) {
        Write-Warning "Conversion incomplete; retained partial VHDX: $output"
    }
}
if ($operationFailure -and $cleanupFailures.Count -eq 0) { throw $operationFailure }
if ($cleanupFailures.Count) {
    if ($operationFailure) { $cleanupFailures.Insert(0,$operationFailure.Exception) }
    throw [AggregateException]::new('Image conversion or owned-mount cleanup failed; inspect inner errors and retained output.', $cleanupFailures)
}
$result = Get-VHD -Path $output
if ($result.Attached -or $result.VhdFormat -ne 'VHDX' -or $result.VhdType -ne 'Dynamic' -or
    $result.ParentPath -or $result.Size -ne $SizeBytes) { throw 'Detached VHDX verification failed.' }
[pscustomobject]@{
    VhdxPath = $output; IsoPath = $iso.FullName; ImageIndex = $selected.Index; ImageName = $selected.Name
    Architecture = $selected.Architecture; Version = $selected.Version; Languages = $selected.Languages
    SizeBytes = $result.Size; Verification = 'Offline layout and boot files checked; detached; not boot-tested'
}
}

function Test-WindowsImageAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-WindowsImageTool {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$Arguments)
    $output = @(& $Path @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Deployment tool $([IO.Path]::GetFileName($Path)) failed ($LASTEXITCODE)." }
    return $output
}

Export-ModuleMember -Function Invoke-WindowsIsoVhdx
