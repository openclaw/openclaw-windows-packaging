[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BundlePath,

    [Parameter(Mandatory)]
    [string]$ApplicationId,

    [Parameter(Mandatory)]
    [guid]$TenantId,

    [Parameter(Mandatory)]
    [string]$SellerId,

    [Parameter(Mandatory)]
    [guid]$ClientId,

    [Parameter(Mandatory)]
    [string]$ClientAssertionFile,

    [string]$PolicyPath = (Join-Path `
        (Split-Path $PSScriptRoot -Parent) `
        'store-submission.json'),

    [string]$EvidencePath,

    [string]$MSStoreCommand = 'msstore'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NonEmptyValue {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [AllowEmptyString()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name must not be empty."
    }
}

function Invoke-MSStore {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$Operation
    )

    & $MSStoreCommand @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Microsoft Store CLI $Operation failed with exit code $LASTEXITCODE."
    }
}

Assert-NonEmptyValue -Name 'ApplicationId' -Value $ApplicationId
Assert-NonEmptyValue -Name 'SellerId' -Value $SellerId
Assert-NonEmptyValue -Name 'MSStoreCommand' -Value $MSStoreCommand

if (-not (Test-Path -LiteralPath $BundlePath -PathType Leaf)) {
    throw "MSIX bundle does not exist: $BundlePath"
}
$resolvedBundle = (Resolve-Path -LiteralPath $BundlePath).Path
if ([IO.Path]::GetExtension($resolvedBundle) -cne '.msixbundle') {
    throw "Store submission requires one .msixbundle: $resolvedBundle"
}
if ((Get-Item -LiteralPath $resolvedBundle).Length -eq 0) {
    throw "MSIX bundle is empty: $resolvedBundle"
}

if (-not (Test-Path -LiteralPath $ClientAssertionFile -PathType Leaf)) {
    throw "OIDC client assertion file does not exist: $ClientAssertionFile"
}
$resolvedAssertion = (Resolve-Path -LiteralPath $ClientAssertionFile).Path
if ([string]::IsNullOrWhiteSpace(
        [IO.File]::ReadAllText($resolvedAssertion))) {
    throw 'OIDC client assertion file is empty.'
}

if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw "Store submission policy does not exist: $PolicyPath"
}
try {
    $policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
}
catch {
    throw "Unable to parse Store submission policy: $($_.Exception.Message)"
}

$requiredProperties = @(
    'schemaVersion'
    'environment'
    'oidcAudience'
    'msstoreCliVersion'
    'commitSubmission'
    'pendingSubmissionPolicy'
    'packageRolloutPercentage'
    'uploadTimeoutSeconds'
)
foreach ($property in $requiredProperties) {
    if ($policy.PSObject.Properties.Name -notcontains $property) {
        throw "Store submission policy is missing '$property'."
    }
}
if ([int]$policy.schemaVersion -ne 1) {
    throw "Unsupported Store submission policy schema: $($policy.schemaVersion)"
}
if ([string]$policy.environment -cne 'microsoft-store') {
    throw 'Store submission policy must use the microsoft-store environment.'
}
if ([string]$policy.oidcAudience -cne 'api://AzureADTokenExchange') {
    throw 'Store submission policy must use the Azure token-exchange audience.'
}
if ([string]$policy.msstoreCliVersion -notmatch '^v\d+\.\d+\.\d+$') {
    throw 'Store submission policy must pin an exact MSStore CLI version.'
}
if ([bool]$policy.commitSubmission -ne $true) {
    throw 'Store submission policy must commit the Partner Center update.'
}
if ([string]$policy.pendingSubmissionPolicy -cne 'replace') {
    throw 'Store submission policy must explicitly replace pending drafts.'
}
$rollout = [float]$policy.packageRolloutPercentage
if ($rollout -lt 0 -or $rollout -gt 100) {
    throw 'Store package rollout percentage must be between 0 and 100.'
}
$uploadTimeout = [long]$policy.uploadTimeoutSeconds
if ($uploadTimeout -lt 100 -or $uploadTimeout -gt 100000) {
    throw 'Store upload timeout must be between 100 and 100000 seconds.'
}

$previousAssertion = [Environment]::GetEnvironmentVariable(
    'MSSTORE_CLIENT_ASSERTION')
$previousAssertionFile = [Environment]::GetEnvironmentVariable(
    'MSSTORE_CLIENT_ASSERTION_FILE')
try {
    [Environment]::SetEnvironmentVariable('MSSTORE_CLIENT_ASSERTION', $null)
    [Environment]::SetEnvironmentVariable(
        'MSSTORE_CLIENT_ASSERTION_FILE',
        $resolvedAssertion)

    Invoke-MSStore -Operation 'configuration' -Arguments @(
        'reconfigure'
        '--tenantId'
        $TenantId.ToString()
        '--sellerId'
        $SellerId
        '--clientId'
        $ClientId.ToString()
        '--clientAssertion'
    )

    Invoke-MSStore -Operation 'publication' -Arguments @(
        'publish'
        $resolvedBundle
        '--appId'
        $ApplicationId
        '--packageRolloutPercentage'
        $rollout.ToString([Globalization.CultureInfo]::InvariantCulture)
        '--uploadTimeout'
        $uploadTimeout.ToString([Globalization.CultureInfo]::InvariantCulture)
    )

    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
        $evidenceDirectory = Split-Path $resolvedEvidence -Parent
        if (-not [string]::IsNullOrEmpty($evidenceDirectory)) {
            New-Item `
                -Path $evidenceDirectory `
                -ItemType Directory `
                -Force |
                Out-Null
        }
        [ordered]@{
            schemaVersion = 1
            applicationId = $ApplicationId
            bundleFileName = [IO.Path]::GetFileName($resolvedBundle)
            bundleSha256 = (
                Get-FileHash -LiteralPath $resolvedBundle -Algorithm SHA256
            ).Hash.ToLowerInvariant()
            msstoreCliVersion = [string]$policy.msstoreCliVersion
            pendingSubmissionPolicy = [string]$policy.pendingSubmissionPolicy
            packageRolloutPercentage = $rollout
            submittedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        } |
            ConvertTo-Json |
            Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        'MSSTORE_CLIENT_ASSERTION',
        $previousAssertion)
    [Environment]::SetEnvironmentVariable(
        'MSSTORE_CLIENT_ASSERTION_FILE',
        $previousAssertionFile)
}
