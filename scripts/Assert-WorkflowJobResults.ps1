[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('pull_request', 'workflow_dispatch', 'push')]
    [string]$EventName,
    [ValidateSet('unsigned', 'test', 'store', 'official')]
    [string]$SigningMode = 'unsigned',
    [string]$Versioning,
    [Parameter(Mandatory)]
    [ValidateSet('success', 'failure', 'cancelled', 'skipped')]
    [string]$ChangesResult,
    [Parameter(Mandatory)]
    [ValidateSet('success', 'failure', 'cancelled', 'skipped')]
    [string]$TestHostResult,
    [Parameter(Mandatory)]
    [ValidateSet('success', 'failure', 'cancelled', 'skipped')]
    [string]$BuildResult,
    [Parameter(Mandatory)]
    [ValidateSet('success', 'failure', 'cancelled', 'skipped')]
    [string]$UpgradeResult
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ChangesResult -cne 'success') {
    throw "Change classification must succeed; result was '$ChangesResult'."
}

$upgradeRequired = (
    ($EventName -ceq 'pull_request' -and $Versioning -ceq 'true') -or
    ($EventName -ceq 'workflow_dispatch' -and $SigningMode -in @('test', 'store', 'official'))
)
if (
    $upgradeRequired -and
    $TestHostResult -ceq 'success' -and
    $BuildResult -ceq 'success' -and
    $UpgradeResult -cne 'success'
) {
    throw "Release candidates and versioning pull requests require successful MSIX upgrade tests; result was '$UpgradeResult'."
}
