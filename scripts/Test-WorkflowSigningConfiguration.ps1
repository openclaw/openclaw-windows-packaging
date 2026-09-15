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
    'name: Test proof-release MSIX upgrades'
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
    'release-assets/*.json'
    'name: Resolve immutable OpenClaw source'
    'name: Restore source snapshot for a retry'
    'name: Save source snapshot'
    'retention-days: 90'
    'ref: ${{ needs.resolve-source.outputs.source_sha }}'
    'EXPECTED_SNAPSHOT_HASH: ${{ needs.resolve-source.outputs.snapshot_sha256 }}'
    'EXPECTED_SOURCE_COMMIT: ${{ needs.resolve-source.outputs.source_sha }}'
    'EXPECTED_PACKAGE_VERSION: ${{ needs.resolve-source.outputs.source_version }}'
    '-ExpectedSourceCommit $env:EXPECTED_SOURCE_COMMIT'
    '-ExpectedPackageVersion $env:EXPECTED_PACKAGE_VERSION'
    'use-actions-cache: "false"'
    'save-actions-cache: "false"'
    'PACKAGE_VERSION: ${{ needs.resolve-source.outputs.package_version }}'
    '-SourceResolutionPath artifacts\source\source-resolution.json'
    '-SourceResolutionSha256 $env:SNAPSHOT_SHA256'
    '-WorkflowRunId $env:GITHUB_RUN_ID'
    'name: Reject duplicate or older official releases'
    'name: Recheck official release version before signing'
    'name: Recheck official release version before publication'
    "group: gateway-msix-`${{ inputs.signing_mode == 'official' && 'official' || github.run_id }}"
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

$cacheModes = [regex]::Matches($workflow, '(?m)^\s*cache-mode:\s*(?<mode>\S+)')
if ($cacheModes.Count -ne 1 -or
    $workflow -notmatch '(?m)^cache-mode: none\s*$' -or
    $workflow -match '(?m)^\s*cache:\s*true\s*$') {
    throw 'Every job must inherit native cache-mode: none, without cache overrides or opt-ins.'
}

if ($workflow.Contains('OPENCLAW_REF:', [StringComparison]::Ordinal) -or
    $workflow -match 'default:\s+[0-9a-f]{40}' -or
    $workflow.Contains('-RequestedRef ', [StringComparison]::Ordinal)) {
    throw 'The workflow must resolve the policy channel, not retain a second default pin or signing ref.'
}
if ($workflow.Contains('--allow-unreleased-changelog', [StringComparison]::Ordinal) -or
    $workflow.Contains('--pnpm-pack', [StringComparison]::Ordinal)) {
    throw 'Use the shared upstream packer options and its defaults, not switches absent from extended stable.'
}
if ($workflow.IndexOf('name: Enforce official signing policy', [StringComparison]::Ordinal) -gt
    $workflow.IndexOf('name: Azure login', [StringComparison]::Ordinal)) {
    throw 'Source and package authorization must precede Azure credentials.'
}
if ($workflow.IndexOf('name: Recheck official release version before signing', [StringComparison]::Ordinal) -gt
    $workflow.IndexOf('name: Azure login', [StringComparison]::Ordinal) -or
    $workflow.IndexOf('name: Recheck official release version before publication', [StringComparison]::Ordinal) -gt
    $workflow.IndexOf('name: Create permanent GitHub release', [StringComparison]::Ordinal)) {
    throw 'Signing and publication retries must recheck duplicate/downgrade protection.'
}

Write-Host 'Gateway MSIX signing workflow configuration passed.'
