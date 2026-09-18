[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Test-OpenClawWorkflowInputs.ps1'
$policy = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'release-policy.json') -Raw |
    ConvertFrom-Json
$approved = [string]$policy.approvedCommit
$development = if ($policy.PSObject.Properties.Name -contains 'developmentCommit') {
    [string]$policy.developmentCommit
}
else {
    ('b' * 40)
}

function Assert-Rejected {
    param([hashtable]$Inputs, [string]$Message)
    try {
        & $scriptPath @Inputs
    }
    catch {
        if ($_.Exception.Message -notmatch $Message) {
            throw "Expected '$Message'; received: $($_.Exception.Message)"
        }
        return
    }
    throw 'Unapproved workflow inputs were accepted.'
}

foreach ($mode in @('unsigned', 'test')) {
    foreach ($ref in @($approved, $development, 'main')) {
        & $scriptPath -SigningMode $mode -RequestedRef $ref `
            -EventName workflow_dispatch -GitRef refs/heads/topic
    }
}
& $scriptPath -SigningMode unsigned -RequestedRef $development `
    -EventName push -GitRef refs/heads/main
& $scriptPath -SigningMode unsigned -RequestedRef $development `
    -EventName pull_request -GitRef refs/pull/1/merge
& $scriptPath -SigningMode official -RequestedRef $approved `
    -EventName workflow_dispatch -GitRef refs/heads/main

foreach ($ref in @($development, [string]$policy.gatewayTag, 'main')) {
    Assert-Rejected @{
        SigningMode = 'official'; RequestedRef = $ref
        EventName = 'workflow_dispatch'; GitRef = 'refs/heads/main'
    } 'Set openclaw_ref.*unsigned.*test'
}
Assert-Rejected @{
    SigningMode = 'official'; RequestedRef = $approved
    EventName = 'workflow_dispatch'; GitRef = 'refs/heads/topic'
} 'manual workflow_dispatch run from main'
Assert-Rejected @{
    SigningMode = 'official'; RequestedRef = $approved
    EventName = 'push'; GitRef = 'refs/heads/main'
} 'manual workflow_dispatch run from main'

Write-Host 'OpenClaw workflow input tests passed.'
