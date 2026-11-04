function ConvertFrom-DismWimInfo {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines)

    $images = [Collections.Generic.List[object]]::new()
    $current = $null
    $key = $null
    foreach ($line in $Lines) {
        if ($line -match '^Index\s*:\s*(\d+)\s*$') {
            $current = [ordered]@{ Index = [int]$Matches[1]; Languages = [Collections.Generic.List[string]]::new() }
            $images.Add($current)
            $key = $null
        } elseif ($null -eq $current -or -not $line.Trim()) {
            $key = $null
        } elseif ($line -match '^(\S[^:]*?)\s*:\s*(.*)$') {
            $key = $Matches[1]
            if ($key -ne 'Languages') { $current[$key] = $Matches[2].Trim() }
        } elseif ($key -eq 'Languages' -and $line -match '^\s+(\S.*?)(\s*\(Default\))?\s*$') {
            $current['Languages'].Add($Matches[1])
        }
    }
    foreach ($image in $images) {
        $version = $image['Version']
        if ($version -and $image['ServicePack Build']) { $version = "$version.$($image['ServicePack Build'])" }
        [pscustomobject]@{
            Index = $image['Index']
            Name = $image['Name']
            Architecture = $image['Architecture']
            Version = $version
            Languages = @($image['Languages'])
        }
    }
}

function Select-WindowsIsoImage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Images,
        [int]$ImageIndex,
        [string]$ImageName
    )
    if ($ImageIndex -and $ImageName) { throw 'Specify ImageIndex or ImageName, not both.' }
    if ($Images.Count -eq 0) { throw 'DISM listed no images.' }
    $selected = @(
        if ($ImageIndex) { $Images | Where-Object Index -EQ $ImageIndex }
        elseif ($ImageName) { $Images | Where-Object Name -IEQ $ImageName }
        elseif ($Images.Count -eq 1) { $Images }
    )
    if ($selected.Count -ne 1) {
        $available = ($Images | ForEach-Object { "$($_.Index): $($_.Name)" }) -join '; '
        throw "Select one image with -ImageName or -ImageIndex. Available: $available"
    }
    return $selected[0]
}

function Assert-WindowsIsoVhdxLayout {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Partitions)
    $types = @(
        'c12a7328-f81f-11d2-ba4b-00a0c93ec93b',
        'e3c9e316-0b5c-4db8-817d-f92df00215ae',
        'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7'
    )
    if ($Partitions.Count -ne 3) { throw 'Expected exactly EFI, MSR and Windows partitions.' }
    foreach ($type in $types) {
        if (@($Partitions | Where-Object { [guid]$_.GptType -eq [guid]$type }).Count -ne 1) {
            throw 'Unexpected GPT partition layout.'
        }
    }
}

function Initialize-WindowsIsoVhdxLayout {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][int]$DiskNumber)
    if (-not $PSCmdlet.ShouldProcess("New VHDX disk $DiskNumber", 'Create GPT/EFI/MSR/Windows partitions')) { return }
    Initialize-Disk -Number $DiskNumber -PartitionStyle GPT | Out-Null
    $initial = @(Get-Partition -DiskNumber $DiskNumber)
    if ($initial.Count -gt 1 -or @($initial | Where-Object {
        [guid]$_.GptType -ne [guid]'e3c9e316-0b5c-4db8-817d-f92df00215ae'
    }).Count) {
        throw 'Unexpected partitions on the new VHDX; refusing to remove them.'
    }
    foreach ($partition in $initial) {
        Remove-Partition -DiskNumber $DiskNumber -PartitionNumber $partition.PartitionNumber -Confirm:$false
    }
    $efi = New-Partition -DiskNumber $DiskNumber -Size 300MB -AssignDriveLetter `
        -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
    $efi | Format-Volume -FileSystem FAT32 -NewFileSystemLabel System -Confirm:$false | Out-Null
    New-Partition -DiskNumber $DiskNumber -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null
    $windows = New-Partition -DiskNumber $DiskNumber -UseMaximumSize -AssignDriveLetter
    $windows | Format-Volume -FileSystem NTFS -NewFileSystemLabel Windows -Confirm:$false | Out-Null
    Assert-WindowsIsoVhdxLayout -Partitions @(Get-Partition -DiskNumber $DiskNumber)
    [pscustomobject]@{ Efi = $efi; Windows = $windows }
}
