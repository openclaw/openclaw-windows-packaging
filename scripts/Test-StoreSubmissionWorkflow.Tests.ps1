[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Get-Content (Join-Path $root '.github/workflows/submit-microsoft-store.yml') -Raw
$step = [regex]::Match($workflow, '(?ms)^    - name: Submit bundle and commit Store update\r?\n.*?^      run: \|\r?\n(?<script>(?:^ {8}[^\r\n]*\r?\n|^\r?\n)+)')
if (-not $step.Success) { throw 'The Store submission step could not be located.' }
$invoke = [scriptblock]::Create([regex]::Replace($step.Groups['script'].Value, '(?m)^ {8}', ''))
$fixture = Join-Path ([IO.Path]::GetTempPath()) "store-workflow-$([guid]::NewGuid().ToString('N'))"
$location = Get-Location
$names = @('EXPECTED_BUNDLE_NAME', 'EXPECTED_BUNDLE_SHA256', 'STORE_CHANNEL',
    'MSSTORE_DEVELOPMENT_FLIGHT_ID', 'MSSTORE_APPLICATION_ID', 'MSSTORE_TENANT_ID',
    'MSSTORE_CLIENT_ID', 'MSSTORE_ASSERTION_FILE')
$original = @{}
foreach ($name in $names) { $original[$name] = [Environment]::GetEnvironmentVariable($name) }
$checks = 0
try {
    New-Item (Join-Path $fixture 'store'), (Join-Path $fixture 'scripts') -ItemType Directory | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture 'release-policy.json'), '{"packageIdentityName":"Fixture.App","publisher":"CN=Fixture"}')
    # Execute the workflow's real validation and argument wiring, but replace
    # its external publisher with a fixture that records only the destination.
    [IO.File]::WriteAllText((Join-Path $fixture 'scripts/Submit-MicrosoftStore.ps1'), @'
param([string]$Channel, [string]$FlightId, [string]$BundlePath)
[IO.File]::WriteAllText((Join-Path $PWD 'called.json'), (@{
    channel = $Channel; flightId = $FlightId; bundle = [IO.Path]::GetFileName($BundlePath)
} | ConvertTo-Json))
'@)
    Set-Location $fixture
    $env:MSSTORE_APPLICATION_ID = 'fixture'
    $env:MSSTORE_TENANT_ID = '11111111-1111-1111-1111-111111111111'
    $env:MSSTORE_CLIENT_ID = '22222222-2222-2222-2222-222222222222'
    $env:MSSTORE_ASSERTION_FILE = 'fixture.jwt'
    $env:MSSTORE_DEVELOPMENT_FLIGHT_ID = '33333333-3333-3333-3333-333333333333'
    foreach ($case in @(
        @{ tag = '2026.11.1-msix.0'; channel = 'production' },
        @{ tag = '2026.11.1-beta.2-msix.0'; channel = 'development' }
    )) {
        $env:STORE_CHANNEL = $case.channel
        $env:EXPECTED_BUNDLE_NAME = "OpenClawGateway-$($case.tag)-store.msixbundle"
        $bundle = Join-Path $fixture "store/$env:EXPECTED_BUNDLE_NAME"
        [IO.File]::WriteAllText($bundle, 'synthetic bundle bytes')
        $env:EXPECTED_BUNDLE_SHA256 = (Get-FileHash $bundle).Hash.ToLowerInvariant()
        & $invoke
        $called = Get-Content called.json -Raw | ConvertFrom-Json
        $expectedFlight = if ($case.channel -eq 'development') { $env:MSSTORE_DEVELOPMENT_FLIGHT_ID } else { '' }
        if ($called.channel -cne $case.channel -or $called.flightId -cne $expectedFlight -or
            $called.bundle -cne $env:EXPECTED_BUNDLE_NAME) { throw 'Workflow changed the destination or selected bundle.' }
        $checks++
        foreach ($mismatch in @('name', 'hash', 'missing-hash')) {
            Remove-Item called.json
            $savedName = $env:EXPECTED_BUNDLE_NAME
            $savedHash = $env:EXPECTED_BUNDLE_SHA256
            if ($mismatch -eq 'name') { $env:EXPECTED_BUNDLE_NAME = 'wrong.msixbundle' }
            elseif ($mismatch -eq 'hash') { $env:EXPECTED_BUNDLE_SHA256 = '0' * 64 }
            else { $env:EXPECTED_BUNDLE_SHA256 = '' }
            $failed = $false
            try { & $invoke } catch { $failed = $true }
            if (-not $failed -or (Test-Path called.json)) { throw 'Mismatched bundle reached the publisher.' }
            $checks++
            $env:EXPECTED_BUNDLE_NAME = $savedName
            $env:EXPECTED_BUNDLE_SHA256 = $savedHash
            # Mark the next call's starting state without invoking the publisher.
            [IO.File]::WriteAllText((Join-Path $fixture 'called.json'), '{}')
        }
        Remove-Item called.json, $bundle
    }
    Write-Host "Store workflow destination and bundle binding: $checks scenarios passed."
}
finally {
    Set-Location $location
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $original[$name]) }
    Remove-Item $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

