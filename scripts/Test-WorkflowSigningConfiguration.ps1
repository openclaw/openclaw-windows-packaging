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
    'name: Test MSBuild property escaping'
    '.\scripts\Test-MSBuildPropertyValue.Tests.ps1'
    'name: Test MSIX upgrade safety'
    '.\scripts\Test-MSIXUpgradeSafety.Tests.ps1'
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
    "if: `${{ github.event_name != 'workflow_dispatch' || (inputs.signing_mode != 'official' && inputs.signing_mode != 'store') }}"
    'uses: actions/cache/restore@v4'
    'name: Save OpenClaw package cache'
    "steps.package-cache.outputs.cache-hit != 'true' && (github.event_name != 'workflow_dispatch' || (inputs.signing_mode != 'official' && inputs.signing_mode != 'store'))"
    'uses: actions/cache/save@v4'
    'name: Restore cached Windows dependency tree'
    'path: ${{ runner.temp }}\openclaw-stage-${{ matrix.architecture }}'
    'key: ${{ steps.payload-key.outputs.key }}'
    'name: Save Windows dependency tree cache'
    "steps.payload-cache.outputs.cache-hit != 'true' && (github.event_name != 'workflow_dispatch' || (inputs.signing_mode != 'official' && inputs.signing_mode != 'store'))"
    'environment: release-signing'
    'id-token: write'
    'uses: azure/login@v3'
    'client-id: ${{ vars.AZURE_CLIENT_ID }}'
    'tenant-id: ${{ vars.AZURE_TENANT_ID }}'
    'subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}'
    'uses: azure/artifact-signing-action@v2'
    'name: Compose unsigned Store and sideload MSIX bundles'
    'name: Upload unsigned Store MSIX bundle'
    'name: Upload unsigned sideload MSIX bundle'
    'name: Test MSIX upgrade (${{ matrix.name }})'
    "needs.changes.outputs.versioning == 'true'"
    "'.github/workflows/gateway-msix.yml'"
    'upgrade_matrix: ${{ steps.filter.outputs.upgrade_matrix }}'
    '.\scripts\Get-MSIXUpgradeMatrix.ps1'
    'matrix: ${{ fromJSON(needs.changes.outputs.upgrade_matrix) }}'
    'scripts/Test-MSIXReleaseIdentity.Tests.ps1'
    'name: Test MSIX upgrade matrix'
    '.\scripts\Test-MSIXUpgradeMatrix.Tests.ps1'
    '.\scripts\msix-upgrade-baselines.json'
    '.\scripts\Test-MSIXUpgrade.ps1'
    '.\scripts\Test-MSIXStoreUpgrade.ps1'
    'fail-fast: false'
    'name: Download unsigned candidate'
    'name: Apply temporary test signature'
    '-IdentityChannel ${{ matrix.channel }}'
    "-TransitionMode '`${{ matrix.transition_mode }}'"
    "-BaselineAssetName '`${{ matrix.baseline_asset }}'"
    "-StoreProductId '9NV70LV3D6XC'"
    '-CandidatePath $candidatePath'
    'openclaw-gateway-msix-upgrade-evidence-${{ matrix.id }}'
    'retention-days: 90'
    '-IdentityChannel $channel'
    '-BundlePath "artifacts\$channelDirectory\bundle\OpenClawGateway.msixbundle"'
    'files-folder-recurse: true'
    'files: ${{ github.workspace }}\artifacts\bundle\OpenClawGateway.msixbundle'
    'publisher: ${{ steps.release.outputs.publisher }}'
    '"publisher=$($policy.sideloadPackageIdentity.publisher)" >> $env:GITHUB_OUTPUT'
    'EXPECTED_PUBLISHER: ${{ needs.authorize-signing.outputs.publisher }}'
    '$expectedSubject = $env:EXPECTED_PUBLISHER'
    'name: Upload signed multi-architecture MSIX bundle'
    'endpoint: https://eus.codesigning.azure.net/'
    'signing-account-name: openclaw'
    'certificate-profile-name: openclaw'
    'name: Publish signed Gateway MSIX release'
    "inputs.publish_release && needs.sign-msix.result == 'success'"
    'name: Retain authorized Microsoft Store submission bundle'
    'name: openclaw-gateway-msix-store-submission'
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

if ($workflow.Contains(
        '-ExpectedBaselineVersion',
        [StringComparison]::Ordinal)) {
    throw 'Store upgrade validation must use the version installed by Microsoft Store.'
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
    throw 'MSIX, bundle and all upgrade verifications must use the same resolved Gateway tag.'
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
foreach ($artifact in @(
    'openclaw-gateway-msix-sideload-unsigned-x64'
    'openclaw-gateway-msix-sideload-unsigned-arm64'
    'openclaw-gateway-msix-sideload-unsigned-bundle'
)) {
    if (-not $signJob.Contains($artifact, [StringComparison]::Ordinal)) {
        throw "Official signing must consume the sideload artifact: $artifact"
    }
}
if ($signJob.Contains(
        'openclaw-gateway-msix-store-unsigned',
        [StringComparison]::Ordinal)) {
    throw 'Official signing must not consume Store-identity packages.'
}

$storePublishJobMatch = [regex]::Match(
    $workflow,
    '(?ms)^  retain-store-submission:\s*(?<job>.*?)(?=^  [a-z][a-z0-9-]+:)'
)
if (-not $storePublishJobMatch.Success) {
    throw 'Unable to locate the retain-store-submission workflow job.'
}
$storePublishJob = $storePublishJobMatch.Groups['job'].Value
foreach ($forbidden in @('azure/login', 'artifact-signing-action', 'id-token: write')) {
    if ($storePublishJob.Contains($forbidden, [StringComparison]::Ordinal)) {
        throw "Store publication must not request signing capability: $forbidden"
    }
}
foreach ($artifact in @(
    'openclaw-gateway-msix-store-unsigned-bundle'
    'openclaw-gateway-msix-store-submission'
    'retention-days: 90'
)) {
    if (-not $storePublishJob.Contains($artifact, [StringComparison]::Ordinal)) {
        throw "Store publication must consume the authorized unsigned artifact: $artifact"
    }
}

if ($workflow.Contains('publish-store-release:', [StringComparison]::Ordinal)) {
    throw 'Unsigned Store packages must not be published as GitHub Releases.'
}

Write-Host 'Gateway MSIX signing workflow configuration passed.'
