[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$workflowPath = Join-Path `
    $repositoryRoot `
    '.github\workflows\gateway-msix.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw

$requiredFragments = @(
    'group: gateway-msix-${{ github.event.pull_request.number || github.run_id }}'
    "cancel-in-progress: `${{ github.event_name == 'pull_request' }}"
    'name: Classify pull request changes'
    '.\scripts\Get-PackagingRelevance.ps1'
    'name: Test packaging relevance'
    "if: `${{ github.event_name != 'pull_request' || needs.changes.outputs.packaging == 'true' }}"
    'name: Gateway MSIX CI'
    "if: `${{ always() }}"
    "contains(needs.*.result, 'failure')"
    "contains(needs.*.result, 'cancelled')"
    'name: Upload payload'
    'name: Test stable source selection'
    'name: Restore source selection for a retry'
    'name: Save immutable source selection'
    'name: openclaw-source-resolution'
    './scripts/Get-WorkflowSource.ps1'
    "'scripts/OpenClawSource.ps1'"
    "'scripts/Get-WorkflowSource.ps1'"
    "'scripts/Test-OpenClawSource.Tests.ps1'"
    '-ReuseSnapshot:($env:GITHUB_RUN_ATTEMPT -ne ''1'')'
    'ref: ${{ steps.resolve.outputs.sha }}'
    '-ExpectedVersion ''${{ steps.resolve.outputs.version }}'''
    'GATEWAY_TAG: ${{ needs.build-package.outputs.source_tag }}'
    '-GatewayTag $env:GATEWAY_TAG'
    'OPENCLAW_REF: ${{ inputs.openclaw_ref || needs.build-package.outputs.source_sha }}'
    "retention-days: `${{ github.event_name == 'pull_request' && 1 || 7 }}"
    'name: Restore cached OpenClaw package'
    "if: `${{ github.event_name != 'workflow_dispatch' || inputs.signing_mode != 'official' }}"
    'uses: actions/cache/restore@v4'
    'name: Save OpenClaw package cache'
    "steps.package-cache.outputs.cache-hit != 'true' && (github.event_name != 'workflow_dispatch' || inputs.signing_mode != 'official')"
    'uses: actions/cache/save@v4'
    'name: Restore cached Windows dependency tree'
    'path: ${{ runner.temp }}\openclaw-stage-${{ matrix.architecture }}'
    'key: ${{ steps.payload-key.outputs.key }}'
    'name: Save Windows dependency tree cache'
    "steps.payload-cache.outputs.cache-hit != 'true' && (github.event_name != 'workflow_dispatch' || inputs.signing_mode != 'official')"
    'environment: release-signing'
    'id-token: write'
    'uses: azure/login@v3'
    'client-id: ${{ vars.AZURE_CLIENT_ID }}'
    'tenant-id: ${{ vars.AZURE_TENANT_ID }}'
    'subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}'
    'uses: azure/artifact-signing-action@v2'
    'name: Compose unsigned multi-architecture MSIX bundle'
    'name: Upload unsigned multi-architecture MSIX bundle'
    'name: Test proof-release MSIX identity transition'
    "needs.changes.outputs.versioning == 'true'"
    'scripts/Test-MSIXReleaseIdentity.Tests.ps1'
    '.\scripts\msix-upgrade-baselines.json'
    '.\scripts\Test-MSIXUpgrade.ps1'
    'name: Download unsigned bundle candidate'
    '-CandidateBundlePath test-signed\bundle\OpenClawGateway.msixbundle'
    'openclaw-gateway-msix-upgrade-evidence'
    'retention-days: 90'
    '-BundlePath artifacts\bundle\OpenClawGateway.msixbundle'
    'files-folder-recurse: true'
    'files: ${{ github.workspace }}\artifacts\bundle\OpenClawGateway.msixbundle'
    'publisher: ${{ steps.release.outputs.publisher }}'
    '"publisher=$($policy.publisher)" >> $env:GITHUB_OUTPUT'
    'EXPECTED_PUBLISHER: ${{ needs.authorize-signing.outputs.publisher }}'
    '$expectedSubject = $env:EXPECTED_PUBLISHER'
    'name: Upload signed multi-architecture MSIX bundle'
    'endpoint: https://eus.codesigning.azure.net/'
    'signing-account-name: openclaw'
    'certificate-profile-name: openclaw'
    'name: Publish signed Gateway MSIX release'
    '.\scripts\Get-MSIXReleaseIdentity.ps1'
    'contents: write'
    'uses: softprops/action-gh-release@v3'
    'tag_name: ${{ needs.authorize-signing.outputs.release_tag }}'
    'target_commitish: ${{ github.sha }}'
    'generate_release_notes: true'
    'overwrite_files: false'
    'fail_on_unmatched_files: true'
    'release-assets/*.msixbundle'
)

foreach ($fragment in $requiredFragments) {
    if (-not $workflow.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Signing workflow is missing required configuration: $fragment"
    }
}

$buildMsixJobMatch = [regex]::Match(
    $workflow,
    '(?ms)^  build-msix:\s*(?<job>.*?)(?=^  [a-z][a-z0-9-]+:)'
)
if (-not $buildMsixJobMatch.Success) {
    throw 'Unable to locate the build-msix workflow job.'
}
$buildMsixJob = $buildMsixJobMatch.Groups['job'].Value
if ($buildMsixJob.Contains(
        'name: Download payload',
        [StringComparison]::Ordinal)) {
    throw 'The build-msix job must compose the locally built payload directly.'
}

$dispatchDefaultMatch = [regex]::Match(
    $workflow,
    '(?ms)openclaw_ref:\s+description:.*?required:\s*false\s+default:\s*''''\s+type:\s*string'
)
if (-not $dispatchDefaultMatch.Success -or
    $workflow -match "(?m)^\s*OPENCLAW_REF:.*\|\|\s*'[0-9a-f]{40}'") {
    throw 'An empty source input must follow stable; do not add a second source pin.'
}

$identityCalls = [regex]::Matches($workflow, '-GatewayTag \$env:GATEWAY_TAG')
if ($identityCalls.Count -ne 3) {
    throw 'MSIX, bundle and upgrade verification must use the same resolved Gateway tag.'
}

if ($workflow.Contains('AZURE_CLIENT_SECRET', [StringComparison]::Ordinal)) {
    throw 'Signing workflow must use OIDC, not an Azure client secret.'
}

$signJobMatch = [regex]::Match(
    $workflow,
    '(?ms)^  sign-msix:\s*(?<job>.*?)(?=^  [a-z][a-z0-9-]+:)'
)
if (-not $signJobMatch.Success) {
    throw 'Unable to locate the sign-msix workflow job.'
}
$signJob = $signJobMatch.Groups['job'].Value
if ($signJob.Contains('release-policy.json', [StringComparison]::Ordinal)) {
    throw (
        'The signing runner must consume the publisher authorized by the ' +
        'authorize-signing job; it does not check out release policy.'
    )
}

Write-Host 'Gateway MSIX signing workflow configuration passed.'
