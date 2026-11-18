[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'Assert-WorkflowJobResults.ps1'

function Assert-Rejected {
    param(
        [hashtable]$Arguments,
        [string]$Pattern = 'require successful MSIX upgrade tests'
    )
    try {
        & $validator @Arguments
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw
        }
        return
    }
    throw 'An eligible release candidate or versioning pull request accepted an unsuccessful upgrade job.'
}

$base = @{
    EventName = 'pull_request'
    Versioning = 'true'
    ChangesResult = 'success'
    TestHostResult = 'success'
    BuildResult = 'success'
}

function Invoke-Validator {
    param([hashtable]$Overrides)
    $arguments = $base.Clone()
    foreach ($entry in $Overrides.GetEnumerator()) {
        $arguments[$entry.Key] = $entry.Value
    }
    & $validator @arguments
}

Invoke-Validator @{ UpgradeResult = 'success' }
foreach ($result in @('skipped', 'failure', 'cancelled')) {
    $arguments = $base.Clone()
    $arguments.UpgradeResult = $result
    Assert-Rejected $arguments
}

Invoke-Validator @{
    EventName = 'workflow_dispatch'
    Versioning = ''
    UpgradeResult = 'skipped'
}
foreach ($mode in @('test', 'store', 'official')) {
    Invoke-Validator @{
        EventName = 'workflow_dispatch'
        SigningMode = $mode
        Versioning = 'true'
        UpgradeResult = 'success'
    }
    foreach ($result in @('skipped', 'failure', 'cancelled')) {
        $arguments = $base.Clone()
        $arguments.EventName = 'workflow_dispatch'
        $arguments.SigningMode = $mode
        $arguments.Versioning = 'false'
        $arguments.UpgradeResult = $result
        Assert-Rejected $arguments
    }
}
Invoke-Validator @{
    EventName = 'push'
    Versioning = 'false'
    UpgradeResult = 'skipped'
}
Invoke-Validator @{
    Versioning = 'false'
    UpgradeResult = 'skipped'
}
Invoke-Validator @{
    TestHostResult = 'skipped'
    UpgradeResult = 'skipped'
}
Invoke-Validator @{
    BuildResult = 'failure'
    UpgradeResult = 'skipped'
}
Assert-Rejected @{
    EventName = 'workflow_dispatch'
    Versioning = ''
    ChangesResult = 'skipped'
    TestHostResult = 'success'
    BuildResult = 'success'
    UpgradeResult = 'skipped'
} 'Change classification must succeed'

# An authorized release must not silently skip executable signing or Store
# retention: the gate catches a job condition that stops matching.
function Join-Arguments {
    param([hashtable[]]$Parts)
    $joined = @{}
    foreach ($part in $Parts) {
        foreach ($entry in $part.GetEnumerator()) { $joined[$entry.Key] = $entry.Value }
    }
    $joined
}
$release = @{
    EventName = 'workflow_dispatch'
    Versioning = 'true'
    UpgradeResult = 'success'
    AuthorizationResult = 'success'
    SigningResult = 'success'
    StoreRetentionResult = 'success'
}
foreach ($mode in @('store', 'official')) {
    Invoke-Validator (Join-Arguments $release, @{ SigningMode = $mode })
    foreach ($result in @('skipped', 'failure', 'cancelled')) {
        Assert-Rejected `
            (Join-Arguments $base, $release, @{ SigningMode = $mode; SigningResult = $result }) `
            'require successful executable and package signing'
        Assert-Rejected `
            (Join-Arguments $base, $release, @{ SigningMode = $mode; StoreRetentionResult = $result }) `
            'require a retained Store submission bundle'
    }
}
# Beta retention follows GitHub publication, and nothing is required before
# authorization succeeds.
Invoke-Validator (Join-Arguments $release, @{
        SigningMode = 'official'
        ReleaseChannel = 'beta'
        PublishResult = 'skipped'
        StoreRetentionResult = 'skipped'
    })
Invoke-Validator (Join-Arguments $release, @{
        SigningMode = 'official'
        AuthorizationResult = 'failure'
        SigningResult = 'skipped'
        StoreRetentionResult = 'skipped'
    })

Write-Output 'Workflow job-result contracts passed.'
