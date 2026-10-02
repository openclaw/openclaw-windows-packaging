[CmdletBinding()]
param(
    [Parameter(Mandatory)][bool]$PackagingExpected,
    [Parameter(Mandatory)][AllowEmptyString()][string]$ExpectedRoute,
    [Parameter(Mandatory)][object]$ExpectedSource,
    [Parameter(Mandatory)][object]$Trusted,
    [Parameter(Mandatory)][object]$ReadOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpenClawSource.ps1')

$trustedResult = Get-OpenClawSourceField $Trusted 'result'
$readResult = Get-OpenClawSourceField $ReadOnly 'result'
foreach ($result in @($trustedResult, $readResult)) {
    if ($result -cnotin @('success', 'failure', 'cancelled', 'skipped')) {
        throw 'The build route has an invalid result.'
    }
}
if (-not $PackagingExpected) {
    if ($ExpectedRoute -cne '' -or $trustedResult -cne 'skipped' -or $readResult -cne 'skipped') {
        throw 'Documentation-only runs must skip both build routes.'
    }
    return
}
if ($ExpectedRoute -ceq 'trusted' -and $trustedResult -ceq 'success' -and $readResult -ceq 'skipped') {
    $selected = $Trusted
}
elseif ($ExpectedRoute -ceq 'read-only' -and $readResult -ceq 'success' -and $trustedResult -ceq 'skipped') {
    $selected = $ReadOnly
}
else { throw 'Expected exactly one successful build matching the validated source route.' }

$outputs = Get-OpenClawSourceField $selected 'outputs'
foreach ($field in @('source_sha', 'source_tag', 'release_identity_tag', 'package_version', 'node_version', 'package_sha256')) {
    $value = Get-OpenClawSourceField $outputs $field
    Assert-OpenClawSourceText $value $field
}
Assert-OpenClawSourceText $outputs.source_sha 'source_sha' -Pattern '\A[0-9a-f]{40}\z'
Assert-OpenClawSourceText $outputs.package_sha256 'package_sha256' -Pattern '\A[0-9a-f]{64}\z'
Assert-OpenClawSourceText $outputs.node_version 'node_version' -Pattern '\A[0-9]+\.[0-9]+\.[0-9]+\z'
Assert-OpenClawSourceVersion $outputs.package_version
if ($outputs.source_tag -cne "v$($outputs.package_version)") {
    throw 'The selected build source tag and package version differ.'
}
foreach ($field in @('source_sha', 'source_tag', 'package_version')) {
    if ($outputs.$field -cne (Get-OpenClawSourceField $ExpectedSource $field)) {
        throw "The selected build differs from the captured source: $field"
    }
}
if ($outputs.release_identity_tag -cne (Get-OpenClawSourceField $ExpectedSource 'release_identity_tag')) {
    throw 'The selected build release identity differs from the captured source.'
}
return $outputs
