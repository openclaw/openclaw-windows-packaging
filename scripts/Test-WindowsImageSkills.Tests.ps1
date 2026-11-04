#Requires -Version 7.0
# Run with Invoke-Pester; every disk, VM and native deployment operation is mocked.
BeforeAll {
    $root = Split-Path $PSScriptRoot -Parent
    $isoScripts = Join-Path $root '.agents\skills\windows-iso-to-vhdx\scripts'
    $templateScripts = Join-Path $root '.agents\skills\crabbox-hyperv-windows\scripts'
    Import-Module (Join-Path $isoScripts 'WindowsIsoVhdx.psm1') -Force
    Import-Module (Join-Path $templateScripts 'WindowsTemplate.psm1') -Force
    . (Join-Path $isoScripts 'WindowsIsoVhdx.Helpers.ps1')
    . (Join-Path $templateScripts 'WindowsTemplate.Helpers.ps1')
    function ConvertTo-TestPassword([string]$Text) {
        $secure = [Security.SecureString]::new()
        foreach ($character in $Text.ToCharArray()) { $secure.AppendChar($character) }
        return $secure
    }
    & (Get-Module WindowsTemplate) {
        function script:Get-Partition {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline)]$InputObject)
            process { [void]$InputObject; throw 'A storage fixture was not configured.' }
        }
        function script:Get-Volume {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline)]$InputObject)
            process { [void]$InputObject; throw 'A volume fixture was not configured.' }
        }
        function script:Set-VM {
            [CmdletBinding(SupportsShouldProcess)]
            param($VM,$ProcessorCount,$AutomaticCheckpointsEnabled,$AutomaticStopAction)
            if ($PSCmdlet.ShouldProcess('Synthetic VM','Configure')) { throw "VM fixture missing: $VM/$ProcessorCount/$AutomaticCheckpointsEnabled/$AutomaticStopAction" }
        }
        function script:Set-VMFirmware {
            [CmdletBinding(SupportsShouldProcess)]
            param($VM,$EnableSecureBoot,$SecureBootTemplate)
            if ($PSCmdlet.ShouldProcess('Synthetic VM','Firmware')) { throw "VM fixture missing: $VM/$EnableSecureBoot/$SecureBootTemplate" }
        }
        function script:Set-VMKeyProtector {
            [CmdletBinding(SupportsShouldProcess)]
            param($VM,[switch]$NewLocalKeyProtector)
            if ($PSCmdlet.ShouldProcess('Synthetic VM','Key protector')) { throw "VM fixture missing: $VM/$NewLocalKeyProtector" }
        }
        function script:Enable-VMTPM {
            [CmdletBinding(SupportsShouldProcess)]
            param($VM)
            if ($PSCmdlet.ShouldProcess('Synthetic VM','TPM')) { throw "VM fixture missing: $VM" }
        }
        function script:Start-VM {
            [CmdletBinding(SupportsShouldProcess)]
            param($VM)
            if ($PSCmdlet.ShouldProcess('Synthetic VM','Start')) { throw "VM fixture missing: $VM" }
        }
        function script:Stop-VM {
            [CmdletBinding(SupportsShouldProcess)]
            param($VM,[switch]$TurnOff,[switch]$Force)
            if ($PSCmdlet.ShouldProcess('Synthetic VM','Stop')) { throw "VM fixture missing: $VM/$TurnOff/$Force" }
        }
        function script:Remove-VM {
            [CmdletBinding(SupportsShouldProcess)]
            param($VM,[switch]$Force)
            if ($PSCmdlet.ShouldProcess('Synthetic VM','Remove')) { throw "VM fixture missing: $VM/$Force" }
        }
    }
    if (-not ('PublicImageFixtureJob' -as [type])) {
        Add-Type @'
using System.Management.Automation;
public sealed class PublicImageFixtureJob : Job {
    public PublicImageFixtureJob() : base("synthetic readiness") { SetJobState(JobState.Running); }
    public override string Location => "fixture";
    public override string StatusMessage => "fixture";
    public override bool HasMoreData => false;
    public override void StopJob() { SetJobState(JobState.Stopped); }
}
'@
    }
}

Describe 'Public image input and WhatIf boundaries' {
    BeforeEach {
        $iso = Join-Path $TestDrive 'input.iso'
        $vhd = Join-Path $TestDrive 'source.vhdx'
        $binary = Join-Path $TestDrive 'crabbox.exe'
        [IO.File]::WriteAllText($iso, 'synthetic media')
        [IO.File]::WriteAllText($vhd, 'synthetic disk')
        [IO.File]::WriteAllText($binary, 'not executable')
    }
    It 'previews ISO conversion without elevation, mounts, disks or native execution' {
        Mock Test-WindowsImageAdministrator { throw 'Elevation must not be queried.' } -ModuleName WindowsIsoVhdx
        $newDisk = Join-Path $TestDrive 'new.vhdx'
        { Invoke-WindowsIsoVhdx -IsoPath $iso -VhdxPath $newDisk -WhatIf } | Should -Not -Throw
        Test-Path -LiteralPath $newDisk | Should -BeFalse
        Should -Invoke Test-WindowsImageAdministrator -ModuleName WindowsIsoVhdx -Times 0
    }
    It 'forwards WhatIf through the public ISO script entry point' {
        $newDisk = Join-Path $TestDrive 'entry-point.vhdx'
        & (Join-Path $isoScripts 'New-WindowsIsoVhdx.ps1') -IsoPath $iso -VhdxPath $newDisk -WhatIf
        Test-Path -LiteralPath $newDisk | Should -BeFalse
    }
    It 'refuses existing disk output before any operation' {
        { Invoke-WindowsIsoVhdx -IsoPath $iso -VhdxPath $vhd -WhatIf } | Should -Throw '*overwrite*'
        [IO.File]::ReadAllText($vhd) | Should -Be 'synthetic disk'
    }
    It 'rejects conflicting edition selectors' {
        { Invoke-WindowsIsoVhdx -IsoPath $iso -VhdxPath (Join-Path $TestDrive 'new.vhdx') -ImageIndex 1 -ImageName Enterprise -WhatIf } |
            Should -Throw '*not both*'
    }
    It 'previews template work without credentials, binary execution, mount or VM' {
        Mock Test-WindowsTemplateAdministrator { throw 'Elevation must not be queried.' } -ModuleName WindowsTemplate
        $output = Join-Path $TestDrive 'private-template'
        { Initialize-CrabboxWindowsTemplate -VhdxPath $vhd -OutputDirectory $output -CrabboxPath $binary -Switch fixture -Prime -WhatIf } |
            Should -Not -Throw
        Test-Path -LiteralPath $output | Should -BeFalse
        Should -Invoke Test-WindowsTemplateAdministrator -ModuleName WindowsTemplate -Times 0
    }
    It 'forwards WhatIf through the public template script entry point without reading a password' {
        $output = Join-Path $TestDrive 'entry-template'
        & (Join-Path $templateScripts 'Initialize-CrabboxHypervWindowsTemplate.ps1') -VhdxPath $vhd `
            -OutputDirectory $output -CrabboxPath $binary -Switch fixture -Prime -WhatIf
        Test-Path -LiteralPath $output | Should -BeFalse
    }
    It 'requires secure input instead of printing or generating a password' {
        { Initialize-CrabboxWindowsTemplate -VhdxPath $vhd -OutputDirectory (Join-Path $TestDrive 'private-template') `
            -CrabboxPath $binary -Switch fixture -Confirm:$false } | Should -Throw '*Read-Host -AsSecureString*'
    }
    It 'previews a private directory without creating it or changing permissions' {
        $private = Join-Path $TestDrive 'preview-private'
        New-CrabboxPrivateDirectory -Path $private -WhatIf
        Test-Path -LiteralPath $private | Should -BeFalse
    }
    It 'restricts a new fixture directory before any credential-bearing file is written' {
        $private = Join-Path $TestDrive 'actual-private-fixture'
        New-CrabboxPrivateDirectory -Path $private -Confirm:$false
        $acl = Get-Acl -LiteralPath $private
        $acl.AreAccessRulesProtected | Should -BeTrue
        $allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-32-544','S-1-5-18')
        foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            $rule.IdentityReference.Value | Should -BeIn $allowed
        }
        { New-CrabboxPrivateDirectory -Path $private -Confirm:$false } | Should -Throw '*new*overwrite*'
    }
    It 'replaces a copied file ACL independently of the containing directory before injecting credentials' {
        $copy = Join-Path $TestDrive 'copied-disk-fixture.vhdx'
        [IO.File]::WriteAllText($copy,'synthetic disk')
        Protect-CrabboxTemplateFile -Path $copy -Confirm:$false
        $acl = Get-Acl -LiteralPath $copy
        $acl.AreAccessRulesProtected | Should -BeTrue
        $allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-32-544','S-1-5-18')
        foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            $rule.IdentityReference.Value | Should -BeIn $allowed
        }
    }
}

Describe 'Image selection and private account document' {
    It 'selects actual exact edition rather than silently choosing the N variant' {
        $lines = @('Index : 1','Name : Windows 11 Enterprise N','Index : 2','Name : Windows 11 Enterprise')
        $images = @(ConvertFrom-DismWimInfo -Lines $lines)
        (Select-WindowsIsoImage -Images $images -ImageName 'Windows 11 Enterprise').Index | Should -Be 2
        { Select-WindowsIsoImage -Images $images } | Should -Throw '*1: Windows 11 Enterprise N*2: Windows 11 Enterprise*'
    }
    It 'parses architecture, servicing version and language metadata' {
        $image = @(ConvertFrom-DismWimInfo -Lines @('Index : 1','Name : Windows 11 Pro','Architecture : arm64',
            'Version : 10.0.1','ServicePack Build : 2','Languages :','  en-US (Default)'))[0]
        $image.Architecture | Should -Be arm64
        $image.Version | Should -Be '10.0.1.2'
        $image.Languages | Should -Contain en-US
    }
    It 'does not initialize partitions if the GPT layout is unexpected' {
        { Assert-WindowsIsoVhdxLayout -Partitions @([pscustomobject]@{ GptType = [guid]::Empty }) } |
            Should -Throw '*exactly EFI*'
    }
    It 'builds valid XML for the selected architecture without plaintext password or extra logon commands' {
        $secret = 'Synthetic<&%fixture-Secret-42'
        $secure = ConvertTo-TestPassword $secret
        foreach ($architecture in @('amd64','arm64')) {
            $document = ConvertTo-CrabboxUnattendXml -AdminUser fixture -AdminPassword $secure -Architecture $architecture
            $document | Should -Not -Match ([regex]::Escape($secret))
            $xml = [xml]$document
            $component = $xml.unattend.settings.component
            $component.processorArchitecture | Should -Be $architecture
            $component.UserAccounts.LocalAccounts.LocalAccount.Name | Should -Be fixture
            $value = $component.UserAccounts.LocalAccounts.LocalAccount.Password.Value
            [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($value)) | Should -Be ($secret + 'Password')
            @($xml.SelectNodes("//*[local-name()='AutoLogon' or local-name()='FirstLogonCommands']")).Count | Should -Be 0
        }
    }
    It 'rejects empty or weak secure input and account/XML injection' {
        $weak = ConvertTo-TestPassword 'short'
        { ConvertTo-CrabboxUnattendXml -AdminUser fixture -AdminPassword $weak -Architecture amd64 } | Should -Throw '*14 characters*'
        $secure = ConvertTo-TestPassword 'Synthetic-Strong-42'
        { ConvertTo-CrabboxUnattendXml -AdminUser '<admin>' -AdminPassword $secure -Architecture amd64 } | Should -Throw '*new local account*'
    }
    It 'reads x64/ARM64 PE headers and rejects unknown architectures without mounting anything' {
        $windows = Join-Path $TestDrive 'Windows'
        New-Item -ItemType Directory -Path (Join-Path $windows 'System32') -Force | Out-Null
        $kernel = Join-Path $windows 'System32\ntoskrnl.exe'
        foreach ($item in @(@(0x8664,'amd64'),@(0xaa64,'arm64'),@(0x014c,'unsupported'))) {
            $bytes = [byte[]]::new(128)
            [BitConverter]::GetBytes([uint16]0x5a4d).CopyTo($bytes,0)
            [BitConverter]::GetBytes([int]64).CopyTo($bytes,0x3c)
            [BitConverter]::GetBytes([uint32]0x4550).CopyTo($bytes,64)
            [BitConverter]::GetBytes([uint16]$item[0]).CopyTo($bytes,68)
            [IO.File]::WriteAllBytes($kernel,$bytes)
            if ($item[1] -eq 'unsupported') {
                { Get-CrabboxWindowsArchitecture -WindowsRoot $windows } | Should -Throw '*x64 and ARM64*'
            } else { Get-CrabboxWindowsArchitecture -WindowsRoot $windows | Should -Be $item[1] }
        }
    }
}

Describe 'Bounded guest readiness probe cleanup' {
    BeforeEach {
        $script:fixtureJob = [PublicImageFixtureJob]::new()
        Mock Invoke-Command { $script:fixtureJob }
        Mock Wait-Job { $null }
        Mock Stop-Job {}
        Mock Remove-Job {}
        Mock Receive-Job { throw 'Must not receive an unfinished probe.' }
    }

    Describe 'Owned template lifecycle with fake Hyper-V and storage operations' {
        BeforeEach {
            $script:source = Join-Path $TestDrive 'owned-source.vhdx'
            $script:binary = Join-Path $TestDrive 'verified.exe'
            $script:destination = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
            $script:expectedVm = [guid]::NewGuid()
            $script:secure = ConvertTo-TestPassword 'Synthetic-Strong-42'
            [IO.File]::WriteAllText($script:source, 'immutable source fixture')
            [IO.File]::WriteAllText($script:binary, 'unexecuted binary fixture')
            Mock Test-WindowsTemplateAdministrator { $true } -ModuleName WindowsTemplate
            Mock Get-CrabboxTemplateExecutable { [pscustomobject]@{Version='fixture';Sha256='fixture'} } -ModuleName WindowsTemplate
            Mock Get-Command {} -ModuleName WindowsTemplate
            Mock Get-VMSwitch {} -ModuleName WindowsTemplate
            Mock Get-VHD { [pscustomobject]@{ Attached=$false;ParentPath='';VhdFormat='VHDX';DiskNumber=99 } } -ModuleName WindowsTemplate
            Mock Get-VHD { [pscustomobject]@{ Attached=$true;ParentPath='';VhdFormat='VHDX';DiskNumber=99 } } `
                -ModuleName WindowsTemplate -ParameterFilter { $Path -like '*windows-template.vhdx' -and $script:mountedFixture }
            Mock New-CrabboxPrivateDirectory { [IO.Directory]::CreateDirectory($Path) | Out-Null } -ModuleName WindowsTemplate
            Mock Mount-VHD { $script:mountedFixture=$true; [pscustomobject]@{DiskNumber=99} } -ModuleName WindowsTemplate
            Mock Dismount-VHD { $script:mountedFixture=$false } -ModuleName WindowsTemplate
            Mock Get-Disk { [pscustomobject]@{Number=99;PartitionStyle='GPT';IsBoot=$false;IsSystem=$false} } -ModuleName WindowsTemplate
            Mock Get-Partition { [pscustomobject]@{ GptType='c12a7328-f81f-11d2-ba4b-00a0c93ec93b'; DriveLetter=$env:SystemDrive.TrimEnd(':') } } -ModuleName WindowsTemplate
            Mock Get-Volume { [pscustomobject]@{DriveLetter=$env:SystemDrive.TrimEnd(':')} } -ModuleName WindowsTemplate
            Mock Test-Path { $true } -ModuleName WindowsTemplate -ParameterFilter { $LiteralPath -like '*:\Windows\System32\Config\SYSTEM' }
            Mock Test-Path { $false } -ModuleName WindowsTemplate -ParameterFilter { $LiteralPath -like '*:\Windows\Panther\unattend.xml' }
            Mock New-Item {} -ModuleName WindowsTemplate
            Mock Write-CrabboxTemplateAnswer {} -ModuleName WindowsTemplate
            Mock Get-CrabboxWindowsArchitecture {
                if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'Arm64') { 'arm64' } else { 'amd64' }
            } -ModuleName WindowsTemplate
            Mock Get-VM {} -ModuleName WindowsTemplate
            Mock Get-VMHardDiskDrive {} -ModuleName WindowsTemplate
            Mock New-VM { [pscustomobject]@{Id=$script:expectedVm;State='Running'} } -ModuleName WindowsTemplate
            Mock Set-VM {} -ModuleName WindowsTemplate
            Mock Set-VMFirmware {} -ModuleName WindowsTemplate
            Mock Set-VMKeyProtector {} -ModuleName WindowsTemplate
            Mock Enable-VMTPM {} -ModuleName WindowsTemplate
            Mock Start-VM {} -ModuleName WindowsTemplate
            Mock Stop-VM {} -ModuleName WindowsTemplate
            Mock Remove-VM {} -ModuleName WindowsTemplate
            Mock Get-VM { [pscustomobject]@{Id=$script:expectedVm;State='Off'} } -ModuleName WindowsTemplate -ParameterFilter { $Id }
            Mock Invoke-CrabboxPrimeProbe { [pscustomobject]@{ ImageState='IMAGE_STATE_COMPLETE';Administrator=$true } } -ModuleName WindowsTemplate
            Mock Start-Sleep { throw 'Fixture must not wait on a real timer.' } -ModuleName WindowsTemplate
        }

        Describe 'Offline conversion ownership with fake deployment tools' {
            BeforeAll {
                & (Get-Module WindowsIsoVhdx) {
                    function script:Get-Volume {
                        [CmdletBinding()]
                        param([Parameter(ValueFromPipeline)]$InputObject)
                        process { [void]$InputObject; throw 'A volume fixture was not configured.' }
                    }
                }
            }
            BeforeEach {
                $script:isoFixture = Join-Path $TestDrive 'owned-media.iso'
                $script:newDisk = Join-Path $TestDrive "$([guid]::NewGuid().ToString('N')).vhdx"
                [IO.File]::WriteAllText($script:isoFixture,'synthetic ISO')
                $script:letters = @('Z','Y','X','W','V','U','T','S','R','Q','P' | Where-Object { -not (Get-PSDrive -Name $_ -ErrorAction SilentlyContinue) } | Select-Object -First 3)
                if ($script:letters.Count -ne 3) { throw 'Three unused fixture drive letters are required.' }
                foreach ($letter in $script:letters) { New-PSDrive -Name $letter -PSProvider FileSystem -Root $TestDrive -Scope Global | Out-Null }
                $script:mediaDrive=$script:letters[0]; $script:efiDrive=$script:letters[1]; $script:windowsDrive=$script:letters[2]
                $script:vhdAttached=$false
                Mock Test-WindowsImageAdministrator { $true } -ModuleName WindowsIsoVhdx
                Mock Get-Command {} -ModuleName WindowsIsoVhdx
                Mock Get-DiskImage { [pscustomobject]@{Attached=$false} } -ModuleName WindowsIsoVhdx
                Mock Mount-DiskImage { [pscustomobject]@{Attached=$true} } -ModuleName WindowsIsoVhdx
                Mock Dismount-DiskImage {} -ModuleName WindowsIsoVhdx
                Mock Get-Volume { [pscustomobject]@{DriveLetter=$script:mediaDrive} } -ModuleName WindowsIsoVhdx
                Mock Test-Path { $true } -ModuleName WindowsIsoVhdx -ParameterFilter { $LiteralPath -like '*:\sources\install.wim' }
                Mock Test-Path { $false } -ModuleName WindowsIsoVhdx -ParameterFilter { $LiteralPath -like '*:\sources\install.esd' }
                Mock Test-Path { $true } -ModuleName WindowsIsoVhdx -ParameterFilter {
                    $LiteralPath -like '*:\Windows\*' -or $LiteralPath -like '*:\EFI\*'
                }
                Mock Invoke-WindowsImageTool {
                    if ($Arguments -contains '/Get-WimInfo') {
                        @('Index : 1','Name : Windows 11 Pro','Architecture : x64','Version : 10.0.1')
                    }
                } -ModuleName WindowsIsoVhdx
                Mock New-VHD { [IO.File]::WriteAllText($Path,'synthetic output disk') } -ModuleName WindowsIsoVhdx
                Mock Mount-VHD { $script:vhdAttached=$true; [pscustomobject]@{DiskNumber=99} } -ModuleName WindowsIsoVhdx
                Mock Dismount-VHD { $script:vhdAttached=$false } -ModuleName WindowsIsoVhdx
                Mock Get-Disk { [pscustomobject]@{Number=99;PartitionStyle='RAW';Size=80GB;IsBoot=$false;IsSystem=$false} } -ModuleName WindowsIsoVhdx
                Mock Get-VHD { [pscustomobject]@{Attached=$script:vhdAttached;DiskNumber=99;ParentPath='';Size=80GB;VhdFormat='VHDX';VhdType='Dynamic'} } -ModuleName WindowsIsoVhdx
                Mock Initialize-WindowsIsoVhdxLayout { [pscustomobject]@{
                    Efi=[pscustomobject]@{DriveLetter=$script:efiDrive}; Windows=[pscustomobject]@{DriveLetter=$script:windowsDrive}
                } } -ModuleName WindowsIsoVhdx
                Mock Get-Partition {
                    @('c12a7328-f81f-11d2-ba4b-00a0c93ec93b','e3c9e316-0b5c-4db8-817d-f92df00215ae','ebd0a0a2-b9e5-4433-87c0-68b6b72699c7') |
                        ForEach-Object { [pscustomobject]@{GptType=$_} }
                } -ModuleName WindowsIsoVhdx
            }
            AfterEach { foreach ($letter in $script:letters) { Remove-PSDrive -Name $letter } }
            It 'applies only the new disk, sends BCDBoot to its EFI and returns detached offline proof' {
                $result = Invoke-WindowsIsoVhdx -IsoPath $script:isoFixture -VhdxPath $script:newDisk
                $result.Verification | Should -Match 'not boot-tested'
                $result.Architecture | Should -Be x64
                Should -Invoke Invoke-WindowsImageTool -Times 1 -ModuleName WindowsIsoVhdx -ParameterFilter {
                    $Path -like '*bcdboot.exe' -and $Arguments[1] -eq '/s' -and
                    $Arguments[2] -eq "$($script:efiDrive):" -and $Arguments[3] -eq '/f' -and $Arguments[4] -eq 'UEFI'
                }
                Should -Invoke Dismount-VHD -Times 1 -ModuleName WindowsIsoVhdx -ParameterFilter { $Path -eq $script:newDisk }
                Should -Invoke Dismount-DiskImage -Times 1 -ModuleName WindowsIsoVhdx -ParameterFilter { $ImagePath -eq $script:isoFixture }
            }
            It 'retains partial output and detaches both owned mounts on DISM apply failure' {
                Mock Invoke-WindowsImageTool { throw 'synthetic apply failure' } -ModuleName WindowsIsoVhdx -ParameterFilter { $Arguments -contains '/Apply-Image' }
                { Invoke-WindowsIsoVhdx -IsoPath $script:isoFixture -VhdxPath $script:newDisk -WarningAction SilentlyContinue } | Should -Throw '*synthetic apply failure*'
                Test-Path -LiteralPath $script:newDisk | Should -BeTrue
                Should -Invoke Dismount-VHD -Times 1 -ModuleName WindowsIsoVhdx -ParameterFilter { $Path -eq $script:newDisk }
                Should -Invoke Dismount-DiskImage -Times 1 -ModuleName WindowsIsoVhdx -ParameterFilter { $ImagePath -eq $script:isoFixture }
            }
            It 'does not initialize a disk whose host-system identity was returned unexpectedly' {
                Mock Get-Disk { [pscustomobject]@{Number=99;PartitionStyle='RAW';Size=80GB;IsBoot=$true;IsSystem=$true} } -ModuleName WindowsIsoVhdx
                { Invoke-WindowsIsoVhdx -IsoPath $script:isoFixture -VhdxPath $script:newDisk -WarningAction SilentlyContinue } | Should -Throw '*identity/RAW-state*'
                Should -Invoke Initialize-WindowsIsoVhdxLayout -Times 0 -ModuleName WindowsIsoVhdx
                Should -Invoke Dismount-VHD -Times 1 -ModuleName WindowsIsoVhdx
            }
            It 'does not detach a preexisting ISO mount on a conversion error' {
                Mock Get-DiskImage { [pscustomobject]@{Attached=$true} } -ModuleName WindowsIsoVhdx
                Mock Invoke-WindowsImageTool { throw 'synthetic enumeration failure' } -ModuleName WindowsIsoVhdx
                { Invoke-WindowsIsoVhdx -IsoPath $script:isoFixture -VhdxPath $script:newDisk } | Should -Throw '*synthetic enumeration failure*'
                Should -Invoke Mount-DiskImage -Times 0 -ModuleName WindowsIsoVhdx
                Should -Invoke Dismount-DiskImage -Times 0 -ModuleName WindowsIsoVhdx
                Should -Invoke New-VHD -Times 0 -ModuleName WindowsIsoVhdx
            }
            It 'keeps the original DISM error and every cleanup error, still attempting both owned detaches' {
                Mock Invoke-WindowsImageTool { throw 'synthetic apply failure' } -ModuleName WindowsIsoVhdx -ParameterFilter { $Arguments -contains '/Apply-Image' }
                Mock Dismount-VHD { throw 'synthetic VHD cleanup failure' } -ModuleName WindowsIsoVhdx
                Mock Dismount-DiskImage { throw 'synthetic ISO cleanup failure' } -ModuleName WindowsIsoVhdx
                $caught = $null
                try { Invoke-WindowsIsoVhdx -IsoPath $script:isoFixture -VhdxPath $script:newDisk -WarningAction SilentlyContinue }
                catch { $caught = $_.Exception }
                $caught | Should -BeOfType ([AggregateException])
                $caught.InnerExceptions.Message | Should -Contain 'synthetic apply failure'
                $caught.InnerExceptions.Message | Should -Contain 'synthetic VHD cleanup failure'
                $caught.InnerExceptions.Message | Should -Contain 'synthetic ISO cleanup failure'
                Should -Invoke Dismount-DiskImage -Times 1 -ModuleName WindowsIsoVhdx
            }
        }
        It 'keeps source bytes and credentials out of result/streams while qualifying only the owned VM' {
            $before = [IO.File]::ReadAllText($script:source)
            $output = @(Initialize-CrabboxWindowsTemplate -VhdxPath $script:source -OutputDirectory $script:destination `
                -CrabboxPath $script:binary -Switch fixture -AdminPassword $script:secure -Prime -Confirm:$false *>&1)
            $result = @($output | Where-Object { $_ -is [pscustomobject] })[-1]
            $result.Verification | Should -Match 'IMAGE_STATE_COMPLETE'
            [IO.File]::ReadAllText($script:source) | Should -Be $before
            ($output | Out-String) | Should -Not -Match 'Synthetic-Strong-42'
            ($result.PSObject.Properties.Name) | Should -Not -Contain Password
            Should -Invoke Remove-VM -Times 1 -ModuleName WindowsTemplate -ParameterFilter { $VM.Id -eq $script:expectedVm }
            Should -Invoke Stop-VM -Times 1 -ModuleName WindowsTemplate -ParameterFilter { $VM.Id -eq $script:expectedVm -and -not $TurnOff }
            Should -Invoke Write-CrabboxTemplateAnswer -Times 1 -ModuleName WindowsTemplate -ParameterFilter {
                $Xml -notmatch 'Synthetic-Strong-42' -and $Xml -match '<PlainText>false</PlainText>'
            }
        }
        It 'cleans up the exact VM on configuration failure and reports failure instead of success' {
            Mock Set-VMFirmware { throw 'synthetic firmware failure' } -ModuleName WindowsTemplate
            Mock Get-VM { [pscustomobject]@{Id=$script:expectedVm;State='Running'} } -ModuleName WindowsTemplate -ParameterFilter { $Id }
            { Initialize-CrabboxWindowsTemplate -VhdxPath $script:source -OutputDirectory $script:destination `
                -CrabboxPath $script:binary -Switch fixture -AdminPassword $script:secure -Prime -Confirm:$false -WarningAction SilentlyContinue } |
                Should -Throw '*synthetic firmware failure*'
            Should -Invoke Stop-VM -Times 1 -ModuleName WindowsTemplate -ParameterFilter { $VM.Id -eq $script:expectedVm -and $TurnOff }
            Should -Invoke Remove-VM -Times 1 -ModuleName WindowsTemplate -ParameterFilter { $VM.Id -eq $script:expectedVm }
        }
        It 'detaches only the copy when writing the answer file fails and retains incomplete output' {
            Mock Write-CrabboxTemplateAnswer { throw 'synthetic write failure' } -ModuleName WindowsTemplate
            { Initialize-CrabboxWindowsTemplate -VhdxPath $script:source -OutputDirectory $script:destination `
                -CrabboxPath $script:binary -Switch fixture -AdminPassword $script:secure -Confirm:$false -WarningAction SilentlyContinue } |
                Should -Throw '*synthetic write failure*'
            Should -Invoke Dismount-VHD -Times 1 -ModuleName WindowsTemplate -ParameterFilter { $Path -like '*windows-template.vhdx' }
            Test-Path -LiteralPath (Join-Path $script:destination 'windows-template.vhdx') | Should -BeTrue
            Should -Invoke New-VM -Times 0 -ModuleName WindowsTemplate
        }
        It 'fails before mounting or writing credentials if the copied file cannot be protected' {
            Mock Protect-CrabboxTemplateFile { throw 'synthetic file ACL failure' } -ModuleName WindowsTemplate
            { Initialize-CrabboxWindowsTemplate -VhdxPath $script:source -OutputDirectory $script:destination `
                -CrabboxPath $script:binary -Switch fixture -AdminPassword $script:secure -Confirm:$false -WarningAction SilentlyContinue } |
                Should -Throw '*synthetic file ACL failure*'
            Should -Invoke Mount-VHD -Times 0 -ModuleName WindowsTemplate
            Should -Invoke Write-CrabboxTemplateAnswer -Times 0 -ModuleName WindowsTemplate
        }
        It 'preserves both a preparation failure and exact-copy cleanup failure' {
            Mock Write-CrabboxTemplateAnswer { throw 'synthetic write failure' } -ModuleName WindowsTemplate
            Mock Dismount-VHD { throw 'synthetic detach failure' } -ModuleName WindowsTemplate
            $caught = $null
            try {
                Initialize-CrabboxWindowsTemplate -VhdxPath $script:source -OutputDirectory $script:destination `
                    -CrabboxPath $script:binary -Switch fixture -AdminPassword $script:secure -Confirm:$false -WarningAction SilentlyContinue
            } catch { $caught = $_.Exception }
            $caught | Should -BeOfType ([AggregateException])
            $caught.InnerExceptions.Message | Should -Contain 'synthetic write failure'
            $caught.InnerExceptions.Message | Should -Contain 'synthetic detach failure'
        }
    }
    It 'stops and removes only its job on a timeout without fabricating readiness' {
        $credential = [pscredential]::new('fixture',(ConvertTo-TestPassword 'Synthetic-Strong-42'))
        $result = Invoke-CrabboxPrimeProbe -VmId ([guid]::NewGuid()) -Credential $credential -TimeoutSeconds 1
        $result | Should -BeNullOrEmpty
        Should -Invoke Stop-Job -Times 1 -ParameterFilter { $Job.Id -eq $script:fixtureJob.Id }
        Should -Invoke Remove-Job -Times 1 -ParameterFilter { $Job.Id -eq $script:fixtureJob.Id }
        Should -Invoke Receive-Job -Times 0
    }
}
