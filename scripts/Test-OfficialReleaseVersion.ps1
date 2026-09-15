[CmdletBinding(DefaultParameterSetName = 'GitHub')]
param(
    [Parameter(Mandatory)]
    [string]$PackageVersion,

    [Parameter(Mandatory, ParameterSetName = 'Tags')]
    [AllowEmptyCollection()]
    [string[]]$ExistingTags,

    [Parameter(ParameterSetName = 'GitHub')]
    [ValidateSet('openclaw/openclaw-windows-packaging')]
    [string]$Repository = 'openclaw/openclaw-windows-packaging'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$version = [version](& (Join-Path $PSScriptRoot 'Get-WorkflowPackageVersion.ps1') `
    -RunNumber 1 -RunAttempt 1 -ReleaseVersion $PackageVersion)
if ($PSCmdlet.ParameterSetName -eq 'GitHub') {
    $ExistingTags = @(& gh api "repos/$Repository/git/matching-refs/tags/v" `
        --paginate --jq '.[].ref')
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to check existing official release versions.'
    }
}
foreach ($tag in $ExistingTags) {
    if ($tag -cmatch '^refs/tags/v(?<version>\d+\.\d+\.\d+\.\d+)$') {
        $existingVersion = [version]$Matches.version
        if ($version -le $existingVersion) {
            throw (
                "MSIX version $version is not newer than existing official tag $tag. " +
                'Do not overwrite or roll back releases; use a newer upstream version ' +
                'or a reviewed packageRevision increase for a packaging-only correction.'
            )
        }
    }
}
