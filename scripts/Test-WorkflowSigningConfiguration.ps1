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
    '.\scripts\Test-OpenClawWorkflowInputs.Tests.ps1'
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
foreach ($fragment in @(
    'runs-on: ${{ matrix.runner }}'
    'architecture: ${{ matrix.architecture }}'
)) {
    if (-not $buildMsixJob.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Payload activation must use the matching runtime architecture: $fragment"
    }
}
foreach ($entry in @(
    @{ Architecture = 'x64'; Runner = 'windows-latest' }
    @{ Architecture = 'arm64'; Runner = 'windows-11-vs2026-arm' }
)) {
    $pattern = 'architecture:\s*' + $entry.Architecture +
        '\s+runner:\s*' + [regex]::Escape($entry.Runner) + '\s'
    if ($buildMsixJob -cnotmatch $pattern) {
        throw "Missing native $($entry.Architecture) payload runner $($entry.Runner)."
    }
}
if ($buildMsixJob.Contains(
        'name: Download payload',
        [StringComparison]::Ordinal)) {
    throw 'The build-msix job must compose the locally built payload directly.'
}

$packageJob = [regex]::Match(
    $workflow,
    '(?ms)^  build-package:\s*(?<job>.*?)(?=^  [a-z][a-z0-9-]+:)'
).Groups['job'].Value
foreach ($fragment in @(
    "SIGNING_MODE: `${{ github.event_name == 'workflow_dispatch' && inputs.signing_mode || 'unsigned' }}"
    '.\scripts\Test-OpenClawWorkflowInputs.ps1'
    '-SigningMode $env:SIGNING_MODE'
    '-RequestedRef $env:OPENCLAW_REF'
    '-EventName $env:GITHUB_EVENT_NAME'
    '-GitRef $env:GITHUB_REF'
)) {
    if (-not $packageJob.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "The source build must validate actual workflow inputs: $fragment"
    }
}
if ($packageJob.IndexOf('Test-OpenClawWorkflowInputs.ps1', [StringComparison]::Ordinal) -ge
    $packageJob.IndexOf('name: Resolve immutable OpenClaw source', [StringComparison]::Ordinal)) {
    throw 'Validate source/signing inputs before resolving or building OpenClaw.'
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
$expectedDefault = [string]$releasePolicy.approvedCommit
if ($releasePolicy.PSObject.Properties.Name -contains 'developmentCommit') {
    $expectedDefault = [string]$releasePolicy.developmentCommit
    if ($expectedDefault -cnotmatch '^[0-9a-f]{40}$' -or
        $expectedDefault -ieq [string]$releasePolicy.approvedCommit) {
        throw 'The development pin must be an immutable commit distinct from the official approval.'
    }
}
$pinnedRevisions = @(
    @(
        $dispatchDefaultMatch.Groups['sha'].Value
        $automaticFallbackMatch.Groups['sha'].Value
        $expectedDefault
    ) | Select-Object -Unique
)
if ($pinnedRevisions.Count -ne 1) {
    throw (
        'The workflow defaults must match the declared development pin, or ' +
        'the official approval when no development pin is declared. ' +
        "Found: $($pinnedRevisions -join ', ')."
    )
}

if ($workflow.Contains('AZURE_CLIENT_SECRET', [StringComparison]::Ordinal)) {
    throw 'Signing workflow must use OIDC, not an Azure client secret.'
}

Write-Host 'Gateway MSIX signing workflow configuration passed.'
