[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ApplicationDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $ApplicationDirectory -ErrorAction Stop).Path
[string[]]$records = @(
    Get-ChildItem -LiteralPath $root -File -Recurse -Force |
        ForEach-Object {
            $relativePath = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            $relativePath + "`t" + $_.Length.ToString(
                [Globalization.CultureInfo]::InvariantCulture)
        }
)
# Ordinal paths and LF delimiters keep the digest independent of locale and enumeration order.
[Array]::Sort($records, [StringComparer]::Ordinal)
$bytes = [Text.Encoding]::UTF8.GetBytes(($records -join "`n") + "`n")
[ordered]@{
    algorithm = 'sha256-path-length-v1'
    fileCount = $records.Count
    sha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}
