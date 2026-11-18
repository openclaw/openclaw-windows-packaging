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
    [string]$UpgradeResult,
    [ValidateSet('stable', 'beta')]
    [string]$ReleaseChannel = 'stable',
    [ValidateSet('success', 'failure', 'cancelled', 'skipped')]
    [string]$AuthorizationResult = 'skipped',
    [ValidateSet('success', 'failure', 'cancelled', 'skipped')]
    [string]$SigningResult = 'skipped',
    [ValidateSet('success', 'failure', 'cancelled', 'skipped')]
    [string]$PublishResult = 'skipped',
    [ValidateSet('success', 'failure', 'cancelled', 'skipped')]
    [string]$StoreRetentionResult = 'skipped'
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

# Authorized release artifacts exist only after their executables are signed,
# so a skipped signing or Store retention job is a missing release, not a pass.
$signingRequired = (
    $EventName -ceq 'workflow_dispatch' -and
    $SigningMode -in @('store', 'official') -and
    $AuthorizationResult -ceq 'success'
)
if ($signingRequired -and $SigningResult -cne 'success') {
    throw "Authorized releases require successful executable and package signing; result was '$SigningResult'."
}
if (
    $signingRequired -and
    ($ReleaseChannel -ceq 'stable' -or $PublishResult -ceq 'success') -and
    $StoreRetentionResult -cne 'success'
) {
    throw "Authorized releases require a retained Store submission bundle; result was '$StoreRetentionResult'."
}
