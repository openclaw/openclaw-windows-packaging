[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$selector = Join-Path $PSScriptRoot 'Select-WorkflowBuild.ps1'
$outputs = [pscustomobject]@{
    source_sha = 'c' * 40
    source_tag = 'v2026.9.7'
    release_identity_tag = 'v2026.9.7'
    package_version = '2026.9.7'
    node_version = '24.21.0'
    package_sha256 = 'a' * 64
}
$expectedSource = $outputs | ConvertTo-Json | ConvertFrom-Json
function New-Result {
    param([string]$Result)
    return [pscustomobject]@{ result = $Result; outputs = $outputs }
}
function Assert-Rejected {
    param([scriptblock]$Action, [string]$Pattern = 'build|source|package|node|Documentation|field')
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        return
    }
    throw 'An invalid build selection was accepted.'
}

foreach ($route in @('trusted', 'read-only')) {
    foreach ($trustedResult in @('success', 'failure', 'cancelled', 'skipped')) {
        foreach ($readResult in @('success', 'failure', 'cancelled', 'skipped')) {
            $parameters = @{
                PackagingExpected = $true; ExpectedRoute = $route
                ExpectedSource = $expectedSource
                Trusted = New-Result $trustedResult; ReadOnly = New-Result $readResult
            }
            $valid = ($route -ceq 'trusted' -and $trustedResult -ceq 'success' -and $readResult -ceq 'skipped') -or
                ($route -ceq 'read-only' -and $readResult -ceq 'success' -and $trustedResult -ceq 'skipped')
            if ($valid) {
                $selected = & $selector @parameters
                foreach ($field in $outputs.PSObject.Properties) {
                    if ($selected.($field.Name) -cne $field.Value) { throw 'The selected build outputs changed.' }
                }
            }
            else { Assert-Rejected { & $selector @parameters } }
        }
    }
}
$skipped = New-Result skipped
if (@(& $selector -PackagingExpected $false -ExpectedRoute '' -ExpectedSource ([pscustomobject]@{}) -Trusted $skipped -ReadOnly $skipped).Count -ne 0) {
    throw 'Documentation-only builds must emit no package outputs.'
}
Assert-Rejected { & $selector -PackagingExpected $false -ExpectedRoute '' -ExpectedSource $expectedSource -Trusted (New-Result success) -ReadOnly $skipped }
Assert-Rejected { & $selector -PackagingExpected $true -ExpectedRoute '' -ExpectedSource $expectedSource -Trusted $skipped -ReadOnly $skipped }
foreach ($field in @('source_sha', 'source_tag', 'package_version', 'node_version', 'package_sha256')) {
    $saved = $outputs.$field
    $outputs.$field = 'invalid'
    Assert-Rejected { & $selector -PackagingExpected $true -ExpectedRoute trusted -ExpectedSource $expectedSource -Trusted (New-Result success) -ReadOnly $skipped }
    $outputs.$field = $saved
}
foreach ($field in @('source_sha', 'source_tag', 'package_version')) {
    $saved = $expectedSource.$field
    $expectedSource.$field = if ($field -ceq 'source_sha') { 'b' * 40 } elseif ($field -ceq 'source_tag') { 'v2026.9.8' } else { '2026.9.8' }
    Assert-Rejected {
        & $selector -PackagingExpected $true -ExpectedRoute trusted -ExpectedSource $expectedSource `
            -Trusted (New-Result success) -ReadOnly $skipped
    } 'differs from the captured source'
    $expectedSource.$field = $saved
}
$savedIdentity = $outputs.release_identity_tag
$outputs.release_identity_tag = 'v2026.9.8'
Assert-Rejected {
    & $selector -PackagingExpected $true -ExpectedRoute trusted -ExpectedSource $expectedSource `
        -Trusted (New-Result success) -ReadOnly $skipped
} 'release identity'
$outputs.release_identity_tag = $savedIdentity
$expectedSource.release_identity_tag = 'v2026.9.8'
$outputs.release_identity_tag = 'v2026.9.8'
$selected = & $selector -PackagingExpected $true -ExpectedRoute trusted -ExpectedSource $expectedSource `
    -Trusted (New-Result success) -ReadOnly $skipped
if ($selected.release_identity_tag -cne 'v2026.9.8') { throw 'The selected release identity changed.' }
$outputs.release_identity_tag = $savedIdentity
$expectedSource.release_identity_tag = $savedIdentity
Assert-Rejected {
    & $selector -PackagingExpected $true -ExpectedRoute trusted -ExpectedSource $expectedSource `
        -Trusted ([pscustomobject]@{ result = 'success'; outputs = [pscustomobject]@{} }) -ReadOnly $skipped
} 'Missing required field'
Write-Host 'Workflow build selection contracts passed (source/signing contracts run in their owning suites).'
