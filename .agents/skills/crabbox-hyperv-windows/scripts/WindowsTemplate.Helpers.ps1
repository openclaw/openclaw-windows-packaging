function ConvertTo-CrabboxUnattendXml {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$AdminUser,
        [Parameter(Mandatory)][Security.SecureString]$AdminPassword,
        [Parameter(Mandatory)][ValidateSet('amd64','arm64')][string]$Architecture
    )
    if ($AdminUser -notmatch '^[A-Za-z][A-Za-z0-9_.-]{0,19}$' -or
        $AdminUser -in @('Administrator','Guest','DefaultAccount','WDAGUtilityAccount')) {
        throw 'Use a new local account name: 1-20 ASCII letters/digits/dot/underscore/hyphen, starting with a letter.'
    }
    if ($AdminPassword.Length -lt 14) { throw 'Use a nonempty strong password of at least 14 characters.' }
    $credential = [pscredential]::new($AdminUser, $AdminPassword)
    $plain = $credential.GetNetworkCredential().Password
    try {
        # Windows Setup obfuscation is reversible, not encryption; the containing disk must stay private.
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($plain + 'Password'))
        $xml = [xml]@"
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="$Architecture" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <OOBE><HideOnlineAccountScreens>true</HideOnlineAccountScreens></OOBE>
      <UserAccounts><LocalAccounts>
        <LocalAccount xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" wcm:action="add">
          <Password><Value>$encoded</Value><PlainText>false</PlainText></Password>
          <Group>Administrators</Group><Name>$AdminUser</Name><DisplayName>$AdminUser</DisplayName>
        </LocalAccount>
      </LocalAccounts></UserAccounts>
    </component>
  </settings>
</unattend>
"@
        return $xml.OuterXml
    } finally { $plain = $null }
}

function New-CrabboxPrivateDirectory {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) { throw 'OutputDirectory must be new; refusing to overwrite or relax an existing directory.' }
    if (-not $PSCmdlet.ShouldProcess($Path, 'Create a private output directory')) { return }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $rights = [Security.AccessControl.FileSystemRights]::FullControl
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $principals = @(Get-CrabboxTemplatePrincipal)
    foreach ($sid in $principals) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, $rights, $inherit, $propagation, [Security.AccessControl.AccessControlType]::Allow))
    }
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path), $acl)
    Assert-CrabboxTemplatePrivacy -Path $Path
}

function Get-CrabboxTemplatePrincipal {
    [Security.Principal.WindowsIdentity]::GetCurrent().User
    [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
}

function Assert-CrabboxTemplatePrivacy {
    [CmdletBinding(DefaultParameterSetName = 'Path')]
    param(
        [Parameter(Mandatory,ParameterSetName='Path')][string]$Path,
        [Parameter(Mandatory,ParameterSetName='Security')][Security.AccessControl.FileSystemSecurity]$Security
    )
    $actual = if ($PSCmdlet.ParameterSetName -eq 'Security') { $Security } else { Get-Acl -LiteralPath $Path }
    if (-not $actual.AreAccessRulesProtected) { throw 'Private output still inherits permissions.' }
    $allowed = @((Get-CrabboxTemplatePrincipal).Value)
    foreach ($rule in $actual.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $allowed) {
            throw 'Unexpected access to private output; no credential material was written.'
        }
    }
}

function Protect-CrabboxTemplateFile {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$Path)
    if (-not $PSCmdlet.ShouldProcess($Path, 'Restrict and verify copied VHDX access before writing credentials')) { return }
    $acl = Get-CrabboxTemplateFileSecurity
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-CrabboxTemplatePrivacy -Path $Path
}

function Get-CrabboxTemplateFileSecurity {
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($sid in @(Get-CrabboxTemplatePrincipal)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow))
    }
    return $acl
}

function Get-CrabboxWindowsArchitecture {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$WindowsRoot)
    $kernel = Join-Path $WindowsRoot 'System32\ntoskrnl.exe'
    foreach ($path in @($WindowsRoot,(Join-Path $WindowsRoot 'System32'),$kernel)) {
        if ((Get-Item -LiteralPath $path -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked Windows image paths are unsupported.'
        }
    }
    $stream = [IO.File]::OpenRead($kernel)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Invalid Windows kernel DOS header.' }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 0 -or $offset -gt $stream.Length - 6) { throw 'Invalid Windows kernel PE offset.' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid Windows kernel PE signature.' }
        switch ($reader.ReadUInt16()) {
            0x8664 { return 'amd64' }
            0xaa64 { return 'arm64' }
            default { throw 'Only x64 and ARM64 Windows templates are supported.' }
        }
    } finally { $reader.Dispose() }
}

function Invoke-CrabboxPrimeProbe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][guid]$VmId,
        [Parameter(Mandatory)][pscredential]$Credential,
        [ValidateRange(1,120)][int]$TimeoutSeconds = 30
    )
    $job = Invoke-Command -VMId $VmId -Credential $Credential -AsJob -ScriptBlock {
        $state = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State').ImageState
        $admin = ([Security.Principal.WindowsPrincipal]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
        [pscustomobject]@{ ImageState = $state; Administrator = $admin }
    }
    try {
        if (-not (Wait-Job -Job $job -Timeout $TimeoutSeconds)) { return $null }
        if ($job.State -ne 'Completed') { return $null }
        return Receive-Job -Job $job -ErrorAction Stop
    } finally {
        if ($job.State -notin @('Completed','Failed','Stopped')) { Stop-Job -Job $job }
        Remove-Job -Job $job -Force
    }
}
