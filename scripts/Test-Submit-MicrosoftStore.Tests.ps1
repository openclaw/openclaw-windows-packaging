[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Submit-MicrosoftStore.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw-store-submission-$([guid]::NewGuid().ToString('N'))"
)
$tenantId = [guid]'11111111-1111-1111-1111-111111111111'
$clientId = [guid]'22222222-2222-2222-2222-222222222222'
$sellerId = '12345678'
$applicationId = '9NTESTOPENCLAW'

function Assert-Fails {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw (
                "Expected failure matching '$MessagePattern'; received: " +
                $_.Exception.Message
            )
        }
        return
    }
    throw "Expected failure matching '$MessagePattern', but the action succeeded."
}

function Invoke-Submission {
    param(
        [string]$PolicyPath = (Join-Path $testRoot 'policy.json'),
        [string]$BundlePath = (Join-Path $testRoot 'OpenClawGateway.msixbundle'),
        [string]$EvidencePath = (Join-Path $testRoot 'evidence.json')
    )

    & $scriptPath `
        -BundlePath $BundlePath `
        -ApplicationId $applicationId `
        -TenantId $tenantId `
        -SellerId $sellerId `
        -ClientId $clientId `
        -ClientAssertionFile (Join-Path $testRoot 'assertion.jwt') `
        -PolicyPath $PolicyPath `
        -EvidencePath $EvidencePath `
        -MSStoreCommand $script:fakeMSStore
}

New-Item -Path $testRoot -ItemType Directory | Out-Null
try {
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'OpenClawGateway.msixbundle'),
        'bundle-fixture')
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'assertion.jwt'),
        'header.payload.signature')
    [ordered]@{
        schemaVersion = 1
        environment = 'microsoft-store'
        oidcAudience = 'api://AzureADTokenExchange'
        msstoreCliVersion = 'v0.4.3'
        commitSubmission = $true
        pendingSubmissionPolicy = 'replace'
        packageRolloutPercentage = 100
        uploadTimeoutSeconds = 1800
    } |
        ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $testRoot 'policy.json') `
            -Encoding utf8

    $script:fakeMSStore = if ($IsWindows) {
        $path = Join-Path $testRoot 'fake-msstore.cmd'
        $command = @'
@echo off
echo %*>>"%FAKE_MSSTORE_LOG%"
if "%1"=="%FAKE_MSSTORE_FAIL_COMMAND%" exit /b 23
exit /b 0
'@
        [IO.File]::WriteAllText(
            $path,
            $command)
        $path
    }
    else {
        $path = Join-Path $testRoot 'fake-msstore'
        $command = @'
#!/bin/sh
printf '%s\n' "$*" >> "$FAKE_MSSTORE_LOG"
if [ "$1" = "$FAKE_MSSTORE_FAIL_COMMAND" ]; then exit 23; fi
exit 0
'@
        [IO.File]::WriteAllText(
            $path,
            $command)
        & chmod +x $path
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to make the fake MSStore CLI executable.'
        }
        $path
    }

    $logPath = Join-Path $testRoot 'msstore.log'
    $env:FAKE_MSSTORE_LOG = $logPath
    $env:FAKE_MSSTORE_FAIL_COMMAND = ''
    $env:MSSTORE_CLIENT_ASSERTION = 'previous-assertion'
    $env:MSSTORE_CLIENT_ASSERTION_FILE = 'previous-file'

    Invoke-Submission

    $calls = @(Get-Content -LiteralPath $logPath)
    if ($calls.Count -ne 2) {
        throw "Expected two MSStore CLI calls; received $($calls.Count)."
    }
    if ($calls[0] -cne (
        "reconfigure --tenantId $tenantId --sellerId $sellerId " +
        "--clientId $clientId --clientAssertion")) {
        throw "Unexpected reconfigure call: $($calls[0])"
    }
    $resolvedBundle = (Resolve-Path -LiteralPath (
        Join-Path $testRoot 'OpenClawGateway.msixbundle')).Path
    if ($calls[1] -cne (
        "publish $resolvedBundle --appId $applicationId " +
        '--packageRolloutPercentage 100 --uploadTimeout 1800')) {
        throw "Unexpected publish call: $($calls[1])"
    }
    if ($calls -match 'header\.payload|assertion\.jwt') {
        throw 'The Store CLI command line exposed the OIDC assertion.'
    }
    if ($env:MSSTORE_CLIENT_ASSERTION -cne 'previous-assertion' -or
        $env:MSSTORE_CLIENT_ASSERTION_FILE -cne 'previous-file') {
        throw 'Store submission did not restore the assertion environment.'
    }

    $evidence = Get-Content `
        -LiteralPath (Join-Path $testRoot 'evidence.json') `
        -Raw |
        ConvertFrom-Json
    $expectedHash = (
        Get-FileHash -LiteralPath $resolvedBundle -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ([string]$evidence.bundleSha256 -cne $expectedHash -or
        [string]$evidence.applicationId -cne $applicationId -or
        [string]$evidence.msstoreCliVersion -cne 'v0.4.3') {
        throw 'Store submission evidence did not bind the submitted bundle.'
    }

    Clear-Content -LiteralPath $logPath
    Remove-Item `
        -LiteralPath (Join-Path $testRoot 'evidence.json') `
        -Force
    $env:FAKE_MSSTORE_FAIL_COMMAND = 'publish'
    Assert-Fails -MessagePattern 'publication failed with exit code 23' -Action {
        Invoke-Submission
    }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'evidence.json')) {
        throw 'A failed Store submission must not write success evidence.'
    }

    $invalidPolicyPath = Join-Path $testRoot 'invalid-policy.json'
    [ordered]@{
        schemaVersion = 1
        environment = 'microsoft-store'
        oidcAudience = 'api://AzureADTokenExchange'
        msstoreCliVersion = 'latest'
        commitSubmission = $true
        pendingSubmissionPolicy = 'replace'
        packageRolloutPercentage = 100
        uploadTimeoutSeconds = 1800
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath $invalidPolicyPath -Encoding utf8
    Assert-Fails -MessagePattern 'pin an exact' -Action {
        Invoke-Submission -PolicyPath $invalidPolicyPath
    }

    $wrongPackagePath = Join-Path $testRoot 'OpenClawGateway-x64.msix'
    [IO.File]::WriteAllText($wrongPackagePath, 'standalone-fixture')
    Assert-Fails -MessagePattern 'requires one .msixbundle' -Action {
        Invoke-Submission -BundlePath $wrongPackagePath
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MSSTORE_LOG -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MSSTORE_FAIL_COMMAND -ErrorAction SilentlyContinue
    Remove-Item Env:MSSTORE_CLIENT_ASSERTION -ErrorAction SilentlyContinue
    Remove-Item Env:MSSTORE_CLIENT_ASSERTION_FILE -ErrorAction SilentlyContinue
}

Write-Host 'Microsoft Store submission tests passed.'
