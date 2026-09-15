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
    'release-assets/*.json'
    'name: Resolve immutable OpenClaw source'
    'name: Restore source snapshot for a retry'
    'name: Save source snapshot'
    'retention-days: 90'
    'ref: ${{ needs.resolve-source.outputs.source_sha }}'
    'EXPECTED_SNAPSHOT_HASH: ${{ needs.resolve-source.outputs.snapshot_sha256 }}'
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

if ($workflow.Contains('AZURE_CLIENT_SECRET', [StringComparison]::Ordinal)) {
    throw 'Signing workflow must use OIDC, not an Azure client secret.'
}

if ($workflow.Contains('OPENCLAW_REF:', [StringComparison]::Ordinal) -or
    $workflow -match 'default:\s+[0-9a-f]{40}' -or
    $workflow.Contains('-RequestedRef ', [StringComparison]::Ordinal)) {
    throw 'The workflow must resolve the policy channel, not retain a second default pin or signing ref.'
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
