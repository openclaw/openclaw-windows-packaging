[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot 'OpenClawSource.ps1'
$workflowPath = Join-Path $PSScriptRoot 'Get-WorkflowSource.ps1'
$policyPath = Join-Path $PSScriptRoot '..\release-policy.json'
$tokens = $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0 -or $ast.BeginBlock -or $ast.ProcessBlock -or
    @($ast.EndBlock.Statements | Where-Object {
            $_ -isnot [Management.Automation.Language.FunctionDefinitionAst]
        }).Count -ne 0) { throw 'The source helper must contain only valid function definitions.' }
if (@(. $sourcePath).Count -ne 0) { throw 'Dot-sourcing the helper must not produce output.' }
$basePolicy = Read-OpenClawReleasePolicy $policyPath
$commit = $basePolicy.approvedCommit
$version = $basePolicy.payloadPackageVersion
$tagObject = '8bec206f3c1f787e1e9c45cfd34d3de2a78c7b8e'
$integrity = 'sha512-' + [Convert]::ToBase64String([byte[]]::new(64))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-source-tests-$([guid]::NewGuid().ToString('N'))"
$testCount = 0
$http = @{ Calls = [Collections.Generic.List[object]]::new(); Responses = @{} }
$originalToken = $env:GH_TOKEN

# Mock the native transport, which survives the workflow script dot-sourcing the helper again.
${function:Invoke-RestMethod} = {
    param($Uri, $Headers, $TimeoutSec, $OperationTimeoutSeconds, $MaximumRedirection, $ErrorAction)
    $key = if ($Uri.StartsWith('https://api.github.com/repos/openclaw/openclaw/', [StringComparison]::Ordinal)) {
        'GitHub:' + $Uri.Substring('https://api.github.com/repos/openclaw/openclaw/'.Length)
    }
    elseif ($Uri.StartsWith('https://registry.npmjs.org/openclaw/', [StringComparison]::Ordinal)) {
        'Registry:' + $Uri.Substring('https://registry.npmjs.org/openclaw/'.Length)
    }
    else { throw 'Unexpected HTTP origin.' }
    $http.Calls.Add([pscustomobject]@{
            Key = $key; Headers = $Headers; Timeout = $TimeoutSec
            OperationTimeout = $OperationTimeoutSeconds; Redirects = $MaximumRedirection; Errors = $ErrorAction
        })
    if (-not $http.Responses.ContainsKey($key)) { throw "Missing offline response: $key" }
    return $http.Responses[$key]
}.GetNewClosure()

function Assert-Equal {
    param($Actual, $Expected)
    if ($Actual -cne $Expected) { throw "Expected '$Expected', got '$Actual'." }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern)
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "Unexpected failure: $($_.Exception.Message)" }
        return
    }
    throw "Expected failure matching '$Pattern'."
}

function Set-Package {
    param([object]$Version = $script:version, [string]$Commit = $script:commit, [string]$Name = 'openclaw')
    $json = @{ name = $Name; version = $Version } | ConvertTo-Json -Compress
    $http.Responses["GitHub:contents/package.json?ref=$Commit"] = [pscustomobject]@{
        type = 'file'; encoding = 'base64'
        content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
        download_url = 'https://untrusted.invalid/never-follow'
    }
}

function Add-Release {
    param([string]$Version = $script:version, [string]$Commit = $script:commit, [string]$Tag = $script:tagObject)
    $http.Responses["Registry:$Version"] = [pscustomobject]@{
        name = 'openclaw'; version = $Version; gitHead = $Commit
        repository = [pscustomobject]@{ type = 'git'; url = 'git+https://github.com/openclaw/openclaw.git' }
        dist = [pscustomobject]@{ integrity = $script:integrity; tarball = 'https://untrusted.invalid/never-follow' }
    }
    $http.Responses["GitHub:git/ref/tags/v$Version"] = [pscustomobject]@{
        ref = "refs/tags/v$Version"; object = [pscustomobject]@{ type = 'tag'; sha = $Tag }
    }
    $http.Responses["GitHub:git/tags/$Tag"] = [pscustomobject]@{
        sha = $Tag; tag = "v$Version"; object = [pscustomobject]@{ type = 'commit'; sha = $Commit }
        verification = [pscustomobject]@{ verified = $true; reason = 'valid' }
        url = 'https://untrusted.invalid/never-follow'
    }
    $http.Responses["GitHub:commits/$Commit"] = [pscustomobject]@{ sha = $Commit }
    Set-Package -Version $Version -Commit $Commit
}

function Invoke-Test {
    param([string]$Name, [scriptblock]$Body)
    $http.Calls.Clear()
    $http.Responses = @{ 'Registry:latest' = [pscustomobject]@{ name = 'openclaw'; version = $version } }
    Add-Release
    $script:policy = Read-OpenClawReleasePolicy $policyPath
    $script:workflow = @{
        PolicyPath = $policyPath; OutputPath = Join-Path $testRoot "source-$testCount.json"
        WorkflowRunId = '123456'; PackagingCommit = 'd' * 40
    }
    & $Body
    $script:testCount++
    Write-Host "PASS: $Name"
}

function Save-Policy {
    $workflow.PolicyPath = Join-Path $testRoot "policy-$testCount.json"
    [IO.File]::WriteAllText($workflow.PolicyPath, ($policy | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $env:GH_TOKEN = 'offline-test-token'
    Invoke-Test 'default policy selects npm latest and verifies the exact signed release' {
        Assert-Equal (Get-OpenClawPolicyRef $policy) 'stable'
        $source = Resolve-OpenClawSource $policy
        Assert-Equal $source.requestedRef 'stable'
        Assert-Equal $source.channel 'stable'
        Assert-Equal $source.packageVersion $version
        Assert-Equal $source.resolvedCommit $commit
        Assert-Equal $source.releaseTag "v$version"
        Assert-Equal $source.tagObject $tagObject
        Assert-Equal $source.registryIntegrity $integrity
        Assert-Equal @($source.PSObject.Properties).Count 9
        Assert-Equal @(Assert-OpenClawSource $source $policy -RequireChannel).Count 0
        Assert-Equal ($http.Calls.Key -join '|') (
            "Registry:latest|Registry:$version|GitHub:git/ref/tags/v$version|" +
            "GitHub:git/tags/$tagObject|GitHub:contents/package.json?ref=$commit")
        foreach ($call in $http.Calls) {
            Assert-Equal $call.Timeout 30
            if ($PSVersionTable.PSVersion -ge [version]'7.4') { Assert-Equal $call.OperationTimeout 30 }
            Assert-Equal $call.Redirects 0
            Assert-Equal $call.Errors 'Stop'
            Assert-Equal $call.Headers.ContainsKey('Authorization') ($call.Key.StartsWith('GitHub:'))
        }
    }
    Invoke-Test 'a new resolution follows an advancing stable channel' {
        $first = Resolve-OpenClawSource $policy
        Add-Release '2026.9.5' ('a' * 40) ('b' * 40)
        $http.Responses['Registry:latest'].version = '2026.9.5'
        $selected = Resolve-OpenClawSource $policy
        Assert-Equal $selected.resolvedCommit ('a' * 40)
        Assert-Equal $first.resolvedCommit $commit
        $identity = & (Join-Path $PSScriptRoot 'Get-MSIXReleaseIdentity.ps1') `
            -GatewayTag $selected.releaseTag -MSIXRevision 0
        Assert-Equal $identity.PackageVersion '2026.9.500.0'
        Assert-Equal $identity.ReleaseTag 'v2026.9.5-msix.0'
    }
    foreach ($missing in @('Registry:latest', "Registry:$version", "GitHub:git/ref/tags/v$version")) {
        Invoke-Test "missing $missing is terminal, with no fallback" {
            Add-Release '2026.6.35' ('e' * 40) ('f' * 40)
            $http.Responses.Remove($missing)
            Assert-Throws { Resolve-OpenClawSource $policy } 'Missing offline response'
            Assert-Equal $http.Calls[-1].Key $missing
            Assert-Equal @(@($http.Calls.Key) -match '2026\.6\.35|extended-stable').Count 0
        }
    }
    foreach ($case in @(
            @{ Name = 'registry name'; Edit = { $http.Responses["Registry:$version"].name = 'other' }; Error = 'exact registry' },
            @{ Name = 'registry version'; Edit = { $http.Responses["Registry:$version"].version = '2026.9.5' }; Error = 'exact registry' },
            @{ Name = 'registry repository'; Edit = { $http.Responses["Registry:$version"].repository.url += '/other' }; Error = 'repository' },
            @{ Name = 'registry integrity'; Edit = { $http.Responses["Registry:$version"].dist.integrity = 'sha512-invalid' }; Error = 'registryIntegrity' },
            @{ Name = 'registry gitHead'; Edit = { $http.Responses["Registry:$version"].gitHead = 'a' * 40 }; Error = 'gitHead' },
            @{ Name = 'lightweight tag'; Edit = { $http.Responses["GitHub:git/ref/tags/v$version"].object.type = 'commit' }; Error = 'annotated tag' },
            @{ Name = 'wrong tag ref'; Edit = { $http.Responses["GitHub:git/ref/tags/v$version"].ref = 'refs/tags/other' }; Error = 'annotated tag' },
            @{ Name = 'unsigned tag'; Edit = { $http.Responses["GitHub:git/tags/$tagObject"].verification.verified = $false }; Error = 'signature' },
            @{ Name = 'nonboolean verification'; Edit = { $http.Responses["GitHub:git/tags/$tagObject"].verification.verified = 'true' }; Error = 'signature' },
            @{ Name = 'wrong tag SHA'; Edit = { $http.Responses["GitHub:git/tags/$tagObject"].sha = 'a' * 40 }; Error = 'signature' },
            @{ Name = 'wrong tag label'; Edit = { $http.Responses["GitHub:git/tags/$tagObject"].tag = 'v2026.9.5' }; Error = 'signature' },
            @{ Name = 'nested tag target'; Edit = { $http.Responses["GitHub:git/tags/$tagObject"].object.type = 'tag' }; Error = 'directly to a commit' },
            @{ Name = 'source name'; Edit = { Set-Package -Name 'other' }; Error = 'source package name' },
            @{ Name = 'source correction mismatch'; Edit = {
                    Add-Release "$version-2"; $http.Responses['Registry:latest'].version = "$version-2"
                    Set-Package $version
                }; Error = 'source package version' }
        )) {
        Invoke-Test "rejects $($case.Name)" { & $case.Edit; Assert-Throws { Resolve-OpenClawSource $policy } $case.Error }
    }
    Invoke-Test 'selected stable versions map to supported MSIX identities; gitHead is optional' {
        foreach ($case in @(
                @{ Version = '2026.9.4'; Build = 400 },
                @{ Version = '2026.9.32'; Build = 3200 },
                @{ Version = '2026.9.4-2'; Build = 420 },
                @{ Version = '2026.9.4-9'; Build = 490 }
            )) {
            $stable = $case.Version
            Add-Release $stable
            $http.Responses["Registry:$stable"].PSObject.Properties.Remove('gitHead')
            $http.Responses['Registry:latest'].version = $stable
            foreach ($ref in @('', $commit)) {
                $selected = Resolve-OpenClawSource $policy -Ref $ref
                Assert-Equal $selected.packageVersion $stable
                foreach ($revision in @(0, 9)) {
                    $identity = & (Join-Path $PSScriptRoot 'Get-MSIXReleaseIdentity.ps1') `
                        -GatewayTag "v$($selected.packageVersion)" -MSIXRevision $revision
                    Assert-Equal $identity.PackageVersion "2026.9.$($case.Build + $revision).0"
                    Assert-Equal $identity.ReleaseTag "v$stable-msix.$revision"
                }
            }
        }
    }
    foreach ($unsupported in @('2026.9.4-1', '2026.9.4-10', '2026.9.4-64')) {
        Invoke-Test "rejects unsupported correction $unsupported during selection and replay" {
            $saved = & $workflowPath @workflow
            Remove-Item -LiteralPath $workflow.OutputPath
            Add-Release $unsupported
            $http.Responses['Registry:latest'].version = $unsupported
            $http.Calls.Clear()
            Assert-Throws { & $workflowPath @workflow } 'correction suffix must be between 2 and 9'
            Assert-Equal $http.Calls.Count 1
            Assert-Equal (Test-Path -LiteralPath $workflow.OutputPath) $false

            $http.Calls.Clear()
            Assert-Throws { & $workflowPath @workflow -Ref $commit } 'correction suffix must be between 2 and 9'
            Assert-Equal $http.Calls.Count 2
            Assert-Equal (Test-Path -LiteralPath $workflow.OutputPath) $false

            $saved.packageVersion = $unsupported
            $saved.releaseTag = "v$unsupported"
            [IO.File]::WriteAllText($workflow.OutputPath, ($saved | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
            $http.Calls.Clear()
            Assert-Throws { & $workflowPath @workflow -ReuseSnapshot } 'correction suffix must be between 2 and 9'
            Assert-Equal $http.Calls.Count 0
            Remove-Item -LiteralPath $workflow.OutputPath

            $policy | Add-Member stableVersion $unsupported
            Save-Policy
            Assert-Throws { & $workflowPath @workflow } 'correction suffix must be between 2 and 9'
            Assert-Equal $http.Calls.Count 0
            Assert-Equal (Test-Path -LiteralPath $workflow.OutputPath) $false
        }
    }
    foreach ($invalid in @('2026.9.33', '2026.6.35-2', '2026.9.4-beta.1', '2026.09.4', '2026.9.4-0', '2026.9.4-01', '2026.9.4+build')) {
        Invoke-Test "rejects $invalid from both latest and a full SHA override" {
            $http.Responses['Registry:latest'].version = $invalid
            Assert-Throws { Resolve-OpenClawSource $policy } 'packageVersion'
            Assert-Equal $http.Calls.Count 1
            Set-Package $invalid
            Assert-Throws { Resolve-OpenClawSource $policy -Ref $commit } 'packageVersion'
        }
    }
    Invoke-Test 'a reviewed older stable pin queries only its exact release and remains officially approved' {
        Add-Release '2026.9.5' ('a' * 40) ('b' * 40)
        $http.Responses['Registry:latest'].version = '2026.9.5'
        $policy | Add-Member stableVersion $version
        Save-Policy
        $source = & $workflowPath @workflow -SigningMode official
        Assert-Equal $source.requestedRef $version
        Assert-Equal $source.resolvedCommit $commit
        Assert-Equal $http.Calls[0].Key "Registry:$version"
        Assert-Equal @(@($http.Calls.Key) -match 'Registry:latest').Count 0
    }
    Invoke-Test 'a withdrawn or mismatched exact pin never falls back' {
        $policy | Add-Member stableVersion '2026.8.31'
        Assert-Throws { Resolve-OpenClawSource $policy } 'Missing offline response'
        Assert-Equal $http.Calls.Count 1
        $http.Responses['Registry:2026.8.31'] = $http.Responses["Registry:$version"]
        Assert-Throws { Resolve-OpenClawSource $policy } 'stableVersion pin'
        Assert-Equal $http.Calls.Count 2
    }
    Invoke-Test 'SHA, tag, and branch overrides stay unsigned provenance and ignore the channel pin' {
        $policy | Add-Member stableVersion '2026.8.31'
        foreach ($ref in @($commit, "v$version", 'feature/source')) {
            $http.Responses["GitHub:commits/$([Uri]::EscapeDataString($ref))"] = [pscustomobject]@{ sha = $commit }
            $source = Resolve-OpenClawSource $policy -Ref $ref
            Assert-Equal $source.requestedRef $ref
            Assert-Equal $source.channel ''
            Assert-Equal $source.packageVersion $version
            Assert-Throws { Assert-OpenClawSource $source $policy -RequireChannel } 'channel-resolved'
        }
        Assert-Equal @(@($http.Calls.Key) -match '^Registry:').Count 0
        $http.Responses["GitHub:commits/$commit"].sha = 'a' * 40
        Assert-Throws { Resolve-OpenClawSource $policy -Ref $commit } 'full SHA override'
    }
    Invoke-Test 'extended-stable selectors and invalid source policies fail before HTTP' {
        foreach ($ref in @('extended-stable', 'refs/heads/extended-stable', 'refs/tags/extended-stable/test')) {
            Assert-Throws { Resolve-OpenClawSource $policy -Ref $ref } 'extended-stable'
        }
        $policy | Add-Member channel 'extended-stable'
        Assert-Throws { Resolve-OpenClawSource $policy } 'channel'
        $policy.channel = 'stable'
        $policy | Add-Member stableVersion '2026.6.35'
        Assert-Throws { Resolve-OpenClawSource $policy } 'packageVersion'
        $policy.PSObject.Properties.Remove('stableVersion')
        $policy.repository += '/other'
        Assert-Throws { Resolve-OpenClawSource $policy } 'repository'
        Assert-Equal $http.Calls.Count 0
    }
    Invoke-Test 'snapshots are write-once UTF8 LF; replay preserves bytes without network' {
        $source = & $workflowPath @workflow
        $bytes = [IO.File]::ReadAllBytes($workflow.OutputPath)
        Assert-Equal $bytes[0] ([byte][char]'{')
        Assert-Equal ($bytes -contains 13) $false
        $http.Calls.Clear()
        $replayed = & $workflowPath @workflow -ReuseSnapshot
        Assert-Equal $replayed.resolvedCommit $source.resolvedCommit
        Assert-Equal ([Convert]::ToBase64String([IO.File]::ReadAllBytes($workflow.OutputPath))) ([Convert]::ToBase64String($bytes))
        Assert-Throws { & $workflowPath @workflow } 'already exists'
        foreach ($field in @('WorkflowRunId', 'PackagingCommit', 'SigningMode', 'Ref')) {
            $changed = $workflow.Clone()
            $changed[$field] = @{ WorkflowRunId = '999'; PackagingCommit = 'e' * 40; SigningMode = 'test'; Ref = "v$version" }[$field]
            Assert-Throws { & $workflowPath @changed -ReuseSnapshot } 'workflow identity|requested selector'
        }
        Assert-Equal $http.Calls.Count 0
    }
    Invoke-Test 'missing snapshots require a new run' {
        Assert-Throws { & $workflowPath @workflow -ReuseSnapshot } 'Start a new workflow run'
        Assert-Equal $http.Calls.Count 0
    }
    Invoke-Test 'changed or withdrawn policy pins cannot replay a saved selector' {
        $policy | Add-Member stableVersion $version
        Save-Policy
        $null = & $workflowPath @workflow
        $http.Calls.Clear()
        $policy.stableVersion = '2026.9.5'
        Save-Policy
        Assert-Throws { & $workflowPath @workflow -ReuseSnapshot } 'requestedRef'
        $policy.PSObject.Properties.Remove('stableVersion')
        Save-Policy
        Assert-Throws { & $workflowPath @workflow -ReuseSnapshot } 'requestedRef'
        Assert-Equal $http.Calls.Count 0
    }
    Invoke-Test 'structurally invalid saved source fields are rejected offline' {
        $source = Resolve-OpenClawSource $policy
        $http.Calls.Clear()
        foreach ($field in @('resolvedCommit', 'packageVersion', 'releaseTag', 'tagObject', 'resolvedAt', 'registryIntegrity')) {
            $changed = $source | ConvertTo-Json | ConvertFrom-Json
            $changed.$field = 'invalid'
            Assert-Throws { Assert-OpenClawSource $changed $policy } $field
        }
        $source.packageVersion = @($version)
        Assert-Throws { Assert-OpenClawSource $source $policy } 'packageVersion'
        $source.packageVersion = $version
        $source.PSObject.Properties.Remove('repository')
        Assert-Throws { Assert-OpenClawSource $source $policy } 'repository'
        Assert-Equal $http.Calls.Count 0
    }
    foreach ($mode in @('official', 'store')) {
        foreach ($ref in @('', $commit)) {
            Invoke-Test "$mode release accepts the reviewed release via selector '$ref'" {
                $source = & $workflowPath @workflow -SigningMode $mode -Ref $ref
                Assert-Equal $source.resolvedCommit $commit
            }
        }
    }
    foreach ($mode in @('official', 'store')) {
        Invoke-Test "a valid newer stable channel is not $mode release authority" {
            Add-Release '2026.9.5' ('a' * 40) ('b' * 40)
            $http.Responses['Registry:latest'].version = '2026.9.5'
            Assert-Throws { & $workflowPath @workflow -SigningMode $mode } 'reviewed approvedCommit'
            Assert-Equal (Test-Path -LiteralPath $workflow.OutputPath) $false
        }
    }
    foreach ($field in @('approvedCommit', 'payloadPackageVersion', 'gatewayTag')) {
        foreach ($mode in @('official', 'store')) {
            Invoke-Test "$mode release still requires the reviewed $field" {
                $policy.$field = @{ approvedCommit = 'a' * 40; payloadPackageVersion = '2026.9.3'; gatewayTag = 'v2026.9.3' }[$field]
                Save-Policy
                Assert-Throws { & $workflowPath @workflow -SigningMode $mode } 'reviewed approvedCommit'
            }
        }
    }
    foreach ($mode in @('official', 'store')) {
        Invoke-Test "$mode explicit inputs must be the full approved SHA, not a tag or branch" {
            foreach ($ref in @("v$version", 'main', ('a' * 40))) {
                Assert-Throws { & $workflowPath @workflow -SigningMode $mode -Ref $ref } 'full reviewed approvedCommit'
            }
            Assert-Equal $http.Calls.Count 0
        }
    }
    Write-Host "Passed $testCount OpenClaw source tests."
}
finally {
    $env:GH_TOKEN = $originalToken
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
