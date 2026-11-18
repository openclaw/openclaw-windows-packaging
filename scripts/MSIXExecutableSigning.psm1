Set-StrictMode -Version Latest

# Executables this repository builds into every Gateway MSIX. They ship with an
# embedded Authenticode signature because the package catalog does not follow a
# file that is copied out of the package: the session host is staged into the
# agent's shared workspace before it runs (SessionHelperStager), where Smart App
# Control and App Control evaluate the copy on its own signature.
function Test-MSIXOwnedExecutable {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [ValidateSet('x64', 'arm64')]
        [string]$Architecture
    )

    return (
        $PackagePath -ceq 'openclaw.exe' -or
        $PackagePath -ceq 'openclaw-gateway-recovery.exe' -or
        $PackagePath.StartsWith(
            "session-host/$Architecture/",
            [StringComparison]::Ordinal
        )
    )
}

function Get-PortableExecutableSecurityLayout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [byte[]]$Bytes
    )

    if (
        $Bytes.Length -lt 0x40 -or
        $Bytes[0] -ne 0x4D -or
        $Bytes[1] -ne 0x5A
    ) {
        return $null
    }

    $peOffset = [BitConverter]::ToInt32($Bytes, 0x3C)
    if (
        $peOffset -lt 0x40 -or
        $peOffset + 24 -gt $Bytes.Length -or
        $Bytes[$peOffset] -ne 0x50 -or
        $Bytes[$peOffset + 1] -ne 0x45 -or
        $Bytes[$peOffset + 2] -ne 0 -or
        $Bytes[$peOffset + 3] -ne 0
    ) {
        return $null
    }

    $coffOffset = $peOffset + 4
    $optionalHeaderSize = [BitConverter]::ToUInt16($Bytes, $coffOffset + 16)
    $optionalOffset = $coffOffset + 20
    if ($optionalOffset + $optionalHeaderSize -gt $Bytes.Length -or $optionalHeaderSize -lt 2) {
        return $null
    }

    $magic = [BitConverter]::ToUInt16($Bytes, $optionalOffset)
    switch ($magic) {
        0x10B {
            $rvaCountOffset = $optionalOffset + 92
            $directoryOffset = $optionalOffset + 96
        }
        0x20B {
            $rvaCountOffset = $optionalOffset + 108
            $directoryOffset = $optionalOffset + 112
        }
        default { return $null }
    }

    $securityEntryOffset = $directoryOffset + (4 * 8)
    if (
        $securityEntryOffset + 8 -gt $optionalOffset + $optionalHeaderSize -or
        [BitConverter]::ToUInt32($Bytes, $rvaCountOffset) -lt 5
    ) {
        return $null
    }

    [pscustomobject]@{
        ChecksumOffset = $optionalOffset + 64
        SecurityEntryOffset = $securityEntryOffset
        CertificateTableOffset = [BitConverter]::ToUInt32($Bytes, $securityEntryOffset)
        CertificateTableSize = [BitConverter]::ToUInt32($Bytes, $securityEntryOffset + 4)
    }
}

# A valid signer must change only what Authenticode itself owns: the optional
# header checksum, the security data directory, and the certificate table
# appended after 8-byte alignment padding. Anything else is a modified binary.
function Assert-AuthenticodeSignatureOnlyChange {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [byte[]]$Original,

        [Parameter(Mandatory)]
        [byte[]]$Signed,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $originalLayout = Get-PortableExecutableSecurityLayout -Bytes $Original
    $signedLayout = Get-PortableExecutableSecurityLayout -Bytes $Signed
    if ($null -eq $originalLayout -or $null -eq $signedLayout) {
        throw "$Name is not a portable executable."
    }
    if (
        $originalLayout.CertificateTableOffset -ne 0 -or
        $originalLayout.CertificateTableSize -ne 0
    ) {
        throw "$Name already carried a signature before signing."
    }
    if (
        $signedLayout.ChecksumOffset -ne $originalLayout.ChecksumOffset -or
        $signedLayout.SecurityEntryOffset -ne $originalLayout.SecurityEntryOffset
    ) {
        throw "$Name changed its headers while being signed."
    }

    $tableOffset = [long]$signedLayout.CertificateTableOffset
    $tableSize = [long]$signedLayout.CertificateTableSize
    if (
        $tableSize -le 0 -or
        $tableOffset -lt $Original.Length -or
        $tableOffset % 8 -ne 0 -or
        $tableOffset - $Original.Length -ge 8 -or
        $tableOffset + $tableSize -ne $Signed.Length
    ) {
        throw "$Name does not end in a single appended Authenticode certificate table."
    }

    for ($index = $Original.Length; $index -lt $tableOffset; $index++) {
        if ($Signed[$index] -ne 0) {
            throw "$Name has non-zero alignment padding before its signature."
        }
    }

    $normalized = [byte[]]::new($Original.Length)
    [Array]::Copy($Signed, $normalized, $Original.Length)
    [Array]::Copy($Original, $originalLayout.ChecksumOffset, $normalized, $originalLayout.ChecksumOffset, 4)
    [Array]::Copy($Original, $originalLayout.SecurityEntryOffset, $normalized, $originalLayout.SecurityEntryOffset, 8)
    $originalHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Original))
    $normalizedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($normalized))
    if ($originalHash -cne $normalizedHash) {
        throw "$Name changed outside its Authenticode signature."
    }
}

# Publisher mode is the release contract: a trusted chain, the reviewed signer
# subject, and an RFC 3161 timestamp so the signature outlives the short-lived
# Artifact Signing certificate. Thumbprint mode serves temporary test
# certificates whose root is deliberately not trusted on the machine.
function Assert-ExecutableSignature {
    [CmdletBinding(DefaultParameterSetName = 'Publisher')]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'Publisher')]
        [string]$ExpectedPublisher,

        [Parameter(Mandatory, ParameterSetName = 'Thumbprint')]
        [string]$ExpectedThumbprint
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature.SignerCertificate -or $signature.Status -eq 'NotSigned') {
        throw "$Name is not Authenticode-signed."
    }
    # A catalog match does not travel with a copied file; only an embedded
    # signature does, so a catalog-backed result is not acceptable here.
    $layout = Get-PortableExecutableSecurityLayout -Bytes ([IO.File]::ReadAllBytes($Path))
    if (
        $signature.SignatureType -ne 'Authenticode' -or
        $null -eq $layout -or
        $layout.CertificateTableSize -eq 0
    ) {
        throw "$Name has no embedded Authenticode signature."
    }

    if ($PSCmdlet.ParameterSetName -ceq 'Publisher') {
        if ($signature.Status -ne 'Valid') {
            throw "$Name signature status was $($signature.Status): $($signature.StatusMessage)"
        }
        if (-not [string]::Equals(
                $signature.SignerCertificate.Subject,
                $ExpectedPublisher,
                [StringComparison]::OrdinalIgnoreCase
            )) {
            throw "$Name signer was unexpected: $($signature.SignerCertificate.Subject)"
        }
        if ($null -eq $signature.TimeStamperCertificate) {
            throw "$Name signature has no timestamp."
        }
    }
    else {
        if ($signature.Status -notin @('Valid', 'UnknownError')) {
            throw "$Name signature status was $($signature.Status): $($signature.StatusMessage)"
        }
        if (-not [string]::Equals(
                $signature.SignerCertificate.Thumbprint,
                $ExpectedThumbprint,
                [StringComparison]::OrdinalIgnoreCase
            )) {
            throw "$Name signer thumbprint was unexpected: $($signature.SignerCertificate.Thumbprint)"
        }
    }

    $signature
}

function Resolve-WindowsSdkTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('MakeAppx.exe', 'signtool.exe')]
        [string]$Name,

        [string]$Path
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return (Resolve-Path -LiteralPath $Path).Path
    }

    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    $candidate = Get-ChildItem `
        -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') `
        -Filter $Name `
        -File `
        -Recurse `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw "$Name was not found in PATH or the Windows 10 SDK."
    }

    $candidate.FullName
}

Export-ModuleMember -Function @(
    'Test-MSIXOwnedExecutable'
    'Get-PortableExecutableSecurityLayout'
    'Assert-AuthenticodeSignatureOnlyChange'
    'Assert-ExecutableSignature'
    'Resolve-WindowsSdkTool'
)
