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

Write-Output 'Workflow job-result contracts passed.'
