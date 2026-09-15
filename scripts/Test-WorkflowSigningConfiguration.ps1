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
    "retention-days: `${{ github.event_name == 'pull_request' && 1 || 7 }}"
    'name: Restore cached OpenClaw package'
    "if: `${{ github.event_name != 'workflow_dispatch' || inputs.signing_mode != 'official' }}"
    'uses: actions/cache/restore@v4'
    'name: Save OpenClaw package cache'
    "steps.package-cache.outputs.cache-hit != 'true' && (github.event_name != 'workflow_dispatch' || inputs.signing_mode != 'official')"
    'uses: actions/cache/save@v4'
    'environment: release-signing'
    'id-token: write'
    'uses: azure/login@v3'
    'client-id: ${{ vars.AZURE_CLIENT_ID }}'
    'tenant-id: ${{ vars.AZURE_TENANT_ID }}'
    'subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}'
    'uses: azure/artifact-signing-action@v2'
    'name: Compose unsigned multi-architecture MSIX bundle'
    'name: Upload unsigned multi-architecture MSIX bundle'
    '-BundlePath artifacts\bundle\OpenClawGateway.msixbundle'
    'files-folder-recurse: true'
    'files: ${{ github.workspace }}\artifacts\bundle\OpenClawGateway.msixbundle'
    'name: Upload signed multi-architecture MSIX bundle'
    'endpoint: https://eus.codesigning.azure.net/'
    'signing-account-name: openclaw'
    'certificate-profile-name: openclaw'
    'name: Publish signed Gateway MSIX release'
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
    '(?ms)openclaw_ref:\s+description:.*?default:\s*(?<sha>[0-9a-f]{40})'
)
$automaticFallbackMatch = [regex]::Match(
    $workflow,
    "OPENCLAW_REF:.*?\|\|\s*'(?<sha>[0-9a-f]{40})'"
)
if (-not $dispatchDefaultMatch.Success -or -not $automaticFallbackMatch.Success) {
    throw 'Unable to locate both pinned OpenClaw workflow revisions.'
}

$releasePolicy = Get-Content `
    -LiteralPath (Join-Path $repositoryRoot 'release-policy.json') `
    -Raw |
    ConvertFrom-Json
$pinnedRevisions = @(
    @(
        $dispatchDefaultMatch.Groups['sha'].Value
        $automaticFallbackMatch.Groups['sha'].Value
        [string]$releasePolicy.approvedCommit
    ) | Select-Object -Unique
)
if ($pinnedRevisions.Count -ne 1) {
    throw (
        'The workflow defaults and official release policy must pin the same ' +
        "OpenClaw commit; found: $($pinnedRevisions -join ', ')."
    )
}

if ($workflow.Contains('AZURE_CLIENT_SECRET', [StringComparison]::Ordinal)) {
    throw 'Signing workflow must use OIDC, not an Azure client secret.'
}

Write-Host 'Gateway MSIX signing workflow configuration passed.'
