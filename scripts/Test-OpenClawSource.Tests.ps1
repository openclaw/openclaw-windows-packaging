[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'OpenClawSource.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0 -or $ast.BeginBlock -or $ast.ProcessBlock -or
    @($ast.EndBlock.Statements | Where-Object {
            $_ -isnot [Management.Automation.Language.FunctionDefinitionAst]
        }).Count -ne 0) {
    throw 'OpenClawSource.ps1 must contain only valid function definitions.'
}
$definitionOutput = @(. $sourcePath)
if ($definitionOutput.Count -ne 0) {
    throw 'Dot-sourcing OpenClawSource.ps1 must not produce output.'
}
$githubTransport = ${function:Invoke-OpenClawGitHubRequest}
$registryTransport = ${function:Invoke-OpenClawRegistryRequest}
$testRoot = Join-Path (Split-Path $PSScriptRoot -Parent) (
    ".openclaw-source-tests-$([guid]::NewGuid().ToString('N'))")
$commit = 'c283867d7cdd1a93cfc58f829c849834c4426d3b'
$tagObject = '8bec206f3c1f787e1e9c45cfd34d3de2a78c7b8e'
$version = '2026.9.4'
$integrity = 'sha512-' + [Convert]::ToBase64String([byte[]]::new(64))
$testCount = 0
$invalidStableVersions = @(
    '2026.09.4', '2026.0.4', '2026.13.4', '2026.9.0', '2026.9.33',
    '2026.6.35', '2026.9.33-1', '2026.9.999', '2026.9.04', '2026.9.4-0',
    '2026.9.4-01', '2026.9.4-beta.1', '2026.9.4-alpha.1', '2026.9.4+build.1',
    '2026.9.4-1+build.1', '2026.9.4.1', 'v2026.9.4', '1.2.3', '0.0.0',
    '2026.9.4?redirect=evil', "2026.9.4`nextra=bad", @('2026.9.4'), 2026
)

function Assert-TestEqual {
    param($Actual, $Expected)

    if ($Actual -cne $Expected) {
        throw "Expected '$Expected', got '$Actual'."
    }
}

function Assert-TestThrows {
    param([scriptblock]$Action, [string]$Pattern)

    try {
        & $Action | Out-Null
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected failure matching '$Pattern', got: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected failure matching '$Pattern', but the action succeeded."
}

function Read-TestPolicy {
    param([string]$Json)

    $path = Join-Path $testRoot 'policy.json'
    [IO.File]::WriteAllText($path, $Json, [Text.UTF8Encoding]::new($false))
    return Read-OpenClawReleasePolicy -Path $path
}

function New-TestPolicy {
    return [pscustomobject]@{
        repository = 'https://github.com/openclaw/openclaw'
        channel = 'stable'
        packageRevision = 0
        publisher = 'CN=OpenClaw Test Publisher'
    }
}

function Set-TestPackage {
    param(
        [string]$Commit = $script:commit,
        [object]$Version = $script:version,
        [string]$Name = 'openclaw'
    )

    $json = @{ name = $Name; version = $Version } | ConvertTo-Json -Compress
    $script:responses["github:contents/package.json?ref=$Commit"] = [pscustomobject]@{
        type = 'file'
        encoding = 'base64'
        content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    }
}

function Add-TestRelease {
    param(
        [string]$Version = $script:version,
        [string]$Commit = $script:commit,
        [string]$TagObject = $script:tagObject
    )

    $script:responses["github:git/ref/tags/v$Version"] = [pscustomobject]@{
        ref = "refs/tags/v$Version"
        object = [pscustomobject]@{ type = 'tag'; sha = $TagObject }
    }
    $script:responses["github:git/tags/$TagObject"] = [pscustomobject]@{
        sha = $TagObject
        tag = "v$Version"
        verification = [pscustomobject]@{ verified = $true; reason = 'valid' }
        object = [pscustomobject]@{ type = 'commit'; sha = $Commit }
        url = 'https://untrusted.example.invalid/ignored-tag-url'
    }
    Set-TestPackage -Commit $Commit -Version $Version
    $script:responses["registry:$Version"] = [pscustomobject]@{
        name = 'openclaw'
        version = $Version
        repository = [pscustomobject]@{
            type = 'git'
            url = 'git+https://github.com/openclaw/openclaw.git'
        }
        dist = [pscustomobject]@{
            integrity = $script:integrity
            tarball = 'https://untrusted.example.invalid/never-download'
        }
        gitHead = $Commit
    }
}

function Reset-TestFixture {
    $script:requests = [Collections.Generic.List[string]]::new()
    $script:httpCalls = [Collections.Generic.List[object]]::new()
    $script:failures = @{}
    $script:responses = @{
        'registry:latest' = [pscustomobject]@{
            name = 'openclaw'
            version = $script:version
        }
        'registry:extended-stable' = [pscustomobject]@{
            name = 'openclaw'
            version = '2026.6.35'
        }
    }
    $script:policy = Read-TestPolicy (New-TestPolicy | ConvertTo-Json)
    Add-TestRelease
    Add-TestRelease -Version '2026.6.35' -Commit ('e' * 40) -TagObject ('f' * 40)
}

function Get-TestResponse {
    param([string]$Key)

    $script:requests.Add($Key)
    if ($script:failures.ContainsKey($Key)) {
        throw $script:failures[$Key]
    }
    if (-not $script:responses.ContainsKey($Key)) {
        throw "No offline fixture for $Key."
    }
    return $script:responses[$Key]
}

# Both service seams and the underlying transport are replaced: no test can use the network.
function Invoke-OpenClawGitHubRequest {
    param([string]$Path)
    return Get-TestResponse "github:$Path"
}

function Invoke-OpenClawRegistryRequest {
    param([string]$Selector)
    return Get-TestResponse "registry:$Selector"
}

function Invoke-RestMethod {
    param($Uri, $Headers, $TimeoutSec, $OperationTimeoutSeconds, $MaximumRedirection, $ErrorAction)

    $script:httpCalls.Add([pscustomobject]@{
            Uri = $Uri
            Headers = $Headers
            TimeoutSec = $TimeoutSec
            OperationTimeoutSeconds = $OperationTimeoutSeconds
            MaximumRedirection = $MaximumRedirection
            ErrorAction = $ErrorAction
        })
    return [pscustomobject]@{ offline = $true }
}

function Invoke-Test {
    param([string]$Name, [scriptblock]$Body)

    Reset-TestFixture
    & $Body
    $script:testCount++
    Write-Host "PASS: $Name"
}

New-Item -Path $testRoot -ItemType Directory | Out-Null
try {
    Invoke-Test 'npm latest resolves a signed annotated regular stable release' {
        $source = Resolve-OpenClawSource -Policy $policy
        Assert-TestEqual ($source -is [System.Management.Automation.PSCustomObject]) $true
        Assert-TestEqual $source.repository $policy.repository
        Assert-TestEqual $source.requestedRef 'stable'
        Assert-TestEqual $source.channel 'stable'
        Assert-TestEqual $source.packageVersion $version
        Assert-TestEqual $source.releaseTag "v$version"
        Assert-TestEqual $source.tagObject $tagObject
        Assert-TestEqual $source.resolvedCommit $commit
        Assert-TestEqual $source.registryIntegrity $integrity
        Assert-TestEqual ($source.resolvedAt.EndsWith('Z')) $true
        Assert-TestEqual ($source.PSObject.Properties.Name -join ',') (
            'repository,requestedRef,resolvedCommit,packageVersion,channel,' +
            'releaseTag,tagObject,resolvedAt,registryIntegrity'
        )
        foreach ($property in $source.PSObject.Properties) {
            Assert-TestEqual ($property.Value -is [string]) $true
        }
        Assert-TestEqual @(Assert-OpenClawSource $source $policy -RequireChannel).Count 0
        Assert-TestEqual ($requests -join '|') (
            "registry:latest|github:git/ref/tags/v$version|" +
            "github:git/tags/$tagObject|github:contents/package.json?ref=$commit|" +
            "registry:$version"
        )
    }

    Invoke-Test 'a new call follows an advancing selector without caching' {
        $first = Resolve-OpenClawSource $policy
        $nextCommit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        Add-TestRelease -Version '2026.9.5' -Commit $nextCommit -TagObject ('b' * 40)
        $responses['registry:latest'].version = '2026.9.5'
        $second = Resolve-OpenClawSource $policy
        Assert-TestEqual $first.resolvedCommit $commit
        Assert-TestEqual $second.resolvedCommit $nextCommit
        Assert-TestEqual $second.packageVersion '2026.9.5'
    }

    Invoke-Test 'unpinned policy ref is logical stable without any HTTP lookup' {
        $refs = @(Get-OpenClawPolicyRef -Policy $policy)
        Assert-TestEqual $refs.Count 1
        Assert-TestEqual $refs[0] 'stable'
        Assert-TestEqual $requests.Count 0
    }

    foreach ($final in @($false, $true)) {
        Invoke-Test 'source version Final compatibility always requires regular stable' {
            foreach ($stableVersion in @($version, "$version-1")) {
                Assert-TestEqual @(
                    Assert-OpenClawSourceVersion -Version $stableVersion -Final:$final
                ).Count 0
            }
            foreach ($badVersion in $invalidStableVersions) {
                Assert-TestThrows {
                    Assert-OpenClawSourceVersion -Version $badVersion -Final:$final
                } 'packageVersion'
            }
        }
    }

    foreach ($pin in @('2026.8.31', '2026.8.31-2')) {
        Invoke-Test "reviewed older stable pin skips latest: $pin" {
            $policy | Add-Member -NotePropertyName stableVersion -NotePropertyValue $pin
            $policy = Read-TestPolicy ($policy | ConvertTo-Json)
            $pinnedCommit = 'a' * 40
            $pinnedTag = 'b' * 40
            Add-TestRelease -Version $pin -Commit $pinnedCommit -TagObject $pinnedTag
            $failures['registry:latest'] = 'A reviewed pin must not query latest.'
            Assert-TestEqual (Get-OpenClawPolicyRef $policy) $pin
            $source = Resolve-OpenClawSource $policy
            Assert-TestEqual $source.channel 'stable'
            Assert-TestEqual $source.requestedRef $pin
            Assert-TestEqual $source.packageVersion $pin
            Assert-TestEqual $source.resolvedCommit $pinnedCommit
            Assert-TestEqual $source.releaseTag "v$pin"
            Assert-TestEqual ($requests -join '|') (
                "registry:$pin|github:git/ref/tags/v$pin|" +
                "github:git/tags/$pinnedTag|github:contents/package.json?ref=$pinnedCommit"
            )
            $replayed = $source | ConvertTo-Json | ConvertFrom-Json
            Assert-TestEqual @(Assert-OpenClawSource $replayed $policy -RequireChannel).Count 0
        }
    }

    foreach ($correctionVersion in @('2026.9.4-1', '2026.12.32-42')) {
        Invoke-Test "latest accepts a verbatim stable numeric correction: $correctionVersion" {
            Add-TestRelease -Version $correctionVersion
            $responses['registry:latest'].version = $correctionVersion
            $source = Resolve-OpenClawSource $policy
            Assert-TestEqual $source.packageVersion $correctionVersion
            Assert-TestEqual $source.releaseTag "v$correctionVersion"
            Assert-TestEqual $source.requestedRef 'stable'
            Assert-TestEqual $source.channel 'stable'
            Assert-TestEqual $requests[$requests.Count - 1] "registry:$correctionVersion"
            Assert-TestEqual @(Assert-OpenClawSource $source $policy -RequireChannel).Count 0
        }
    }

    foreach ($pinned in @($false, $true)) {
        Invoke-Test 'same-source npm correction cannot silently rewrite the source version' {
            $correctionVersion = "$version-1"
            Add-TestRelease -Version $correctionVersion
            Set-TestPackage -Version $version
            if ($pinned) {
                $policy | Add-Member -NotePropertyName stableVersion -NotePropertyValue $correctionVersion
            }
            else {
                $responses['registry:latest'].version = $correctionVersion
            }
            Assert-TestThrows { Resolve-OpenClawSource $policy } 'source package version'
            Assert-TestEqual $requests.Count 4
            Assert-TestEqual ($requests -match 'extended-stable|2026\.6\.35').Count 0
        }
    }

    Invoke-Test 'a withdrawn registry pin fails without latest or cross-channel fallback' {
        $pin = '2026.8.31'
        $policy | Add-Member -NotePropertyName stableVersion -NotePropertyValue $pin
        Assert-TestThrows { Resolve-OpenClawSource $policy } 'No offline fixture'
        Assert-TestEqual $requests.Count 1
        Assert-TestEqual $requests[0] "registry:$pin"
    }

    Invoke-Test 'an exact pin response cannot select a different version' {
        $pin = '2026.8.31'
        $policy | Add-Member -NotePropertyName stableVersion -NotePropertyValue $pin
        Add-TestRelease -Version $pin
        $responses["registry:$pin"].version = $version
        Assert-TestThrows { Resolve-OpenClawSource $policy } 'stableVersion pin'
        Assert-TestEqual $requests.Count 1
    }

    foreach ($withdrawn in @($false, $true)) {
        Invoke-Test 'changed or withdrawn policy pins reject old snapshots on replay' {
            $policy | Add-Member -NotePropertyName stableVersion -NotePropertyValue $version
            $source = Resolve-OpenClawSource $policy | ConvertTo-Json | ConvertFrom-Json
            if ($withdrawn) {
                $policy.PSObject.Properties.Remove('stableVersion')
            }
            else {
                $policy.stableVersion = '2026.9.3'
            }
            $requestCount = $requests.Count
            Assert-TestThrows { Assert-OpenClawSource $source $policy -RequireChannel } 'requestedRef'
            if (-not $withdrawn) {
                $source.requestedRef = $policy.stableVersion
                Assert-TestThrows { Assert-OpenClawSource $source $policy -RequireChannel } 'stableVersion pin'
            }
            Assert-TestEqual $requests.Count $requestCount
        }
    }

    foreach ($endpoint in @(
            'registry:latest', "github:git/ref/tags/v$version",
            "github:git/tags/$tagObject", "github:contents/package.json?ref=$commit",
            "registry:$version"
        )) {
        Invoke-Test "API failure is terminal at $endpoint" {
            $failures[$endpoint] = 'Offline fixture API failure.'
            Assert-TestThrows { Resolve-OpenClawSource $policy } 'Offline fixture API failure'
            Assert-TestEqual $requests[$requests.Count - 1] $endpoint
            Assert-TestEqual ($requests -match 'extended-stable|2026\.6\.35').Count 0
        }
    }

    Invoke-Test 'missing latest never falls back to an available extended-stable release' {
        $responses.Remove('registry:latest')
        Assert-TestThrows { Resolve-OpenClawSource $policy } 'No offline fixture'
        Assert-TestEqual $requests.Count 1
        Assert-TestEqual $requests[0] 'registry:latest'
    }

    foreach ($badVersion in $invalidStableVersions) {
        Invoke-Test 'latest rejects invalid or extended versions without fallback' {
            $responses['registry:latest'].version = $badVersion
            Assert-TestThrows { Resolve-OpenClawSource $policy } 'packageVersion'
            Assert-TestEqual $requests.Count 1
            Assert-TestEqual $requests[0] 'registry:latest'
        }
    }

    Invoke-Test 'channel package identity must match' {
        $responses['registry:latest'].name = 'other'
        Assert-TestThrows { Resolve-OpenClawSource $policy } 'openclaw package'
    }

    foreach ($case in @(
            @{ Field = 'ref'; Value = 'refs/tags/v2026.9.3'; Error = 'exact annotated' },
            @{ Field = 'type'; Value = 'commit'; Error = 'exact annotated' },
            @{ Field = 'sha'; Value = '../../other'; Error = 'tag object' }
        )) {
        Invoke-Test "malformed tag ref rejects $($case.Field)" {
            $tagRef = $responses["github:git/ref/tags/v$version"]
            if ($case.Field -eq 'ref') {
                $tagRef.ref = $case.Value
            }
            else {
                $tagRef.object.($case.Field) = $case.Value
            }
            Assert-TestThrows { Resolve-OpenClawSource $policy } $case.Error
            Assert-TestEqual $requests.Count 2
        }
    }

    foreach ($case in @(
            @{ Field = 'sha'; Value = ('f' * 40) },
            @{ Field = 'tag'; Value = 'v2026.9.3' },
            @{ Field = 'verified'; Value = $false },
            @{ Field = 'verified'; Value = 'true' },
            @{ Field = 'verified'; Value = @($true) },
            @{ Field = 'reason'; Value = 'unsigned' }
        )) {
        Invoke-Test "unverified or mismatched annotated tag rejects $($case.Field)" {
            $tag = $responses["github:git/tags/$tagObject"]
            if ($case.Field -in @('verified', 'reason')) {
                $tag.verification.($case.Field) = $case.Value
            }
            else {
                $tag.($case.Field) = $case.Value
            }
            Assert-TestThrows { Resolve-OpenClawSource $policy } 'GitHub-verified signature'
        }
    }

    Invoke-Test 'nested annotated tags are not commit targets' {
        $responses["github:git/tags/$tagObject"].object.type = 'tag'
        Assert-TestThrows { Resolve-OpenClawSource $policy } 'directly to a commit'
    }

    Invoke-Test 'invalid commit is rejected before package request' {
        $responses["github:git/tags/$tagObject"].object.sha = 'not-a-sha'
        Assert-TestThrows { Resolve-OpenClawSource $policy } "'commit'"
        Assert-TestEqual $requests.Count 3
    }

    Invoke-Test 'GitHub SHA casing is normalized' {
        $responses["github:git/ref/tags/v$version"].object.sha = $tagObject.ToUpperInvariant()
        $responses["github:git/tags/$tagObject"].sha = $tagObject.ToUpperInvariant()
        $responses["github:git/tags/$tagObject"].object.sha = $commit.ToUpperInvariant()
        $responses["registry:$version"].gitHead = $commit.ToUpperInvariant()
        $source = Resolve-OpenClawSource $policy
        Assert-TestEqual $source.resolvedCommit $commit
        Assert-TestEqual $source.tagObject $tagObject
    }

    Invoke-Test 'immutable package version must match the selection' {
        Set-TestPackage -Version '2026.9.3'
        Assert-TestThrows { Resolve-OpenClawSource $policy } 'source package version'
        Assert-TestEqual ($requests -match 'extended-stable|2026\.6\.35').Count 0
    }

    Invoke-Test 'exact registry version must match the selection' {
        $responses["registry:$version"].version = '2026.9.5'
        Assert-TestThrows { Resolve-OpenClawSource $policy } 'exact registry manifest'
        Assert-TestEqual ($requests -match 'extended-stable|2026\.6\.35').Count 0
    }

    Invoke-Test 'exact registry package name must match' {
        $responses["registry:$version"].name = 'other'
        Assert-TestThrows { Resolve-OpenClawSource $policy } 'exact registry manifest'
    }

    foreach ($repository in @(
            'https://github.com/other/openclaw',
            'git+https://github.com/openclaw/openclaw.git#main',
            'https://github.com/openclaw/openclaw/extra',
            'git@github.com:openclaw/openclaw.git'
        )) {
        Invoke-Test "unexpected npm repository is rejected: $repository" {
            $responses["registry:$version"].repository.url = $repository
            Assert-TestThrows { Resolve-OpenClawSource $policy } 'repository'
        }
    }

    Invoke-Test 'canonical npm repository string is accepted' {
        $responses["registry:$version"].repository = $policy.repository
        $source = Resolve-OpenClawSource $policy
        Assert-TestEqual $source.resolvedCommit $commit
    }

    Invoke-Test 'absent optional gitHead is accepted' {
        $responses["registry:$version"].PSObject.Properties.Remove('gitHead')
        $source = Resolve-OpenClawSource $policy
        Assert-TestEqual $source.resolvedCommit $commit
    }

    foreach ($gitHead in @(('f' * 40), 'bad', $null, @($commit))) {
        Invoke-Test "present gitHead must match: $($gitHead -join ',')" {
            $responses["registry:$version"].gitHead = $gitHead
            Assert-TestThrows { Resolve-OpenClawSource $policy } 'gitHead'
        }
    }

    foreach ($badIntegrity in @(
            '', ('sha256-' + [Convert]::ToBase64String([byte[]]::new(32))),
            ('sha512-' + [Convert]::ToBase64String([byte[]]::new(63))),
            ('sha512-' + ('A' * 85) + 'B=='), "$integrity`n", @($integrity)
        )) {
        Invoke-Test 'invalid or noncanonical SHA-512 integrity is rejected' {
            $responses["registry:$version"].dist.integrity = $badIntegrity
            Assert-TestThrows { Resolve-OpenClawSource $policy } 'registryIntegrity'
        }
    }

    foreach ($ref in @('feature/source', "v$version", $commit, 'stable')) {
        Invoke-Test "explicit override resolves only GitHub commit and source: $ref" {
            $escapedRef = [Uri]::EscapeDataString($ref)
            $responses["github:commits/$escapedRef"] = [pscustomobject]@{
                sha = $commit.ToUpperInvariant()
            }
            Set-TestPackage -Version '2026.9.4-2'
            $source = Resolve-OpenClawSource $policy -Ref $ref
            Assert-TestEqual $source.requestedRef $ref
            Assert-TestEqual $source.resolvedCommit $commit
            Assert-TestEqual $source.packageVersion '2026.9.4-2'
            foreach ($field in @('channel', 'releaseTag', 'tagObject', 'registryIntegrity')) {
                Assert-TestEqual $source.$field ''
            }
            Assert-TestEqual ($requests -join '|') (
                "github:commits/$escapedRef|github:contents/package.json?ref=$commit"
            )
            Assert-TestEqual @(Assert-OpenClawSource $source $policy).Count 0
            Assert-TestThrows {
                Assert-OpenClawSource $source $policy -RequireChannel
            } 'channel-resolved source'
            $replayed = $source | ConvertTo-Json | ConvertFrom-Json
            Assert-TestEqual @(Assert-OpenClawSource $replayed $policy).Count 0
            Assert-TestThrows {
                Assert-OpenClawSource $replayed $policy -RequireChannel
            } 'channel-resolved source'
        }
    }

    foreach ($ref in @(' ', "`t", 'branch name', "main`nINJECT=value", "main$([char]0)", "main$([char]0x2028)")) {
        Invoke-Test 'whitespace and control characters in overrides are rejected before HTTP' {
            Assert-TestThrows { Resolve-OpenClawSource $policy -Ref $ref } "'Ref'"
            Assert-TestEqual $requests.Count 0
        }
    }

    foreach ($ref in @(
            'extended-stable', 'extended-stable/2026.9', 'Extended-Stable',
            'refs/heads/extended-stable/2026.9', 'refs/tags/extended-stable'
        )) {
        Invoke-Test 'explicit extended-stable selectors are rejected before networking' {
            Assert-TestThrows { Resolve-OpenClawSource $policy -Ref $ref } "'Ref'.*extended-stable"
            Assert-TestEqual $requests.Count 0
            $source = Resolve-OpenClawSource $policy
            foreach ($field in @('channel', 'releaseTag', 'tagObject', 'registryIntegrity')) {
                $source.$field = ''
            }
            $source.requestedRef = $ref
            Assert-TestThrows { Assert-OpenClawSource $source $policy } "'requestedRef'.*extended-stable"
        }
    }

    foreach ($badVersion in $invalidStableVersions) {
        Invoke-Test 'override source version must belong to regular stable' {
            $responses['github:commits/main'] = [pscustomobject]@{ sha = $commit }
            Set-TestPackage -Version $badVersion
            Assert-TestThrows { Resolve-OpenClawSource $policy -Ref 'main' } 'packageVersion'
        }
    }

    Invoke-Test 'override source package name must be openclaw' {
        $responses['github:commits/main'] = [pscustomobject]@{ sha = $commit }
        Set-TestPackage -Name 'other'
        Assert-TestThrows { Resolve-OpenClawSource $policy -Ref 'main' } 'source package name'
    }

    Invoke-Test 'an immutable SHA override cannot opt into an extended-stable source version' {
        $responses["github:commits/$commit"] = [pscustomobject]@{ sha = $commit }
        Set-TestPackage -Version '2026.6.35'
        Assert-TestThrows { Resolve-OpenClawSource $policy -Ref $commit } 'packageVersion'
        Assert-TestEqual ($requests -join '|') (
            "github:commits/$commit|github:contents/package.json?ref=$commit"
        )
    }

    foreach ($badVersion in $invalidStableVersions) {
        Invoke-Test 'channel and override snapshots both require regular stable versions' {
            $source = Resolve-OpenClawSource $policy
            $source.packageVersion = $badVersion
            Assert-TestThrows { Assert-OpenClawSource $source $policy } 'packageVersion'
            foreach ($field in @('channel', 'releaseTag', 'tagObject', 'registryIntegrity')) {
                $source.$field = ''
            }
            $source.requestedRef = 'main'
            Assert-TestThrows { Assert-OpenClawSource $source $policy } 'packageVersion'
        }
    }

    Invoke-Test 'a previous extended-stable snapshot is rejected' {
        $source = Resolve-OpenClawSource $policy
        $source.requestedRef = 'extended-stable'
        $source.channel = 'extended-stable'
        $source.packageVersion = '2026.6.35'
        $source.releaseTag = 'v2026.6.35'
        Assert-TestThrows { Assert-OpenClawSource $source $policy } 'extended-stable'
    }

    Invoke-Test 'snapshot timestamp can be old and extra build metadata is allowed' {
        $source = Resolve-OpenClawSource $policy
        $source.resolvedAt = '2000-01-01T00:00:00Z'
        $source | Add-Member -NotePropertyName nodeVersion -NotePropertyValue '24.16.0'
        Assert-TestEqual @(Assert-OpenClawSource $source $policy -RequireChannel).Count 0
    }

    Invoke-Test 'snapshot survives default JSON timestamp conversion without mutation' {
        $source = Resolve-OpenClawSource $policy
        $replayed = $source | ConvertTo-Json | ConvertFrom-Json
        Assert-TestEqual ($replayed.resolvedAt -is [DateTime]) $true
        Assert-TestEqual $replayed.resolvedAt.Kind ([DateTimeKind]::Utc)
        Assert-TestEqual $replayed.resolvedAt.ToString('o') $source.resolvedAt
        Assert-TestEqual @(Assert-OpenClawSource $replayed $policy -RequireChannel).Count 0
        Assert-TestEqual ($replayed.resolvedAt -is [DateTime]) $true
        $replayedAgain = $replayed | ConvertTo-Json | ConvertFrom-Json
        Assert-TestEqual (
            $replayedAgain.resolvedAt | ConvertTo-Json -Compress
        ) ($replayed.resolvedAt | ConvertTo-Json -Compress)
    }

    Invoke-Test 'UTC DateTime timestamps may be old' {
        $source = Resolve-OpenClawSource $policy
        $source.resolvedAt = [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
        Assert-TestEqual @(Assert-OpenClawSource $source $policy -RequireChannel).Count 0
    }

    foreach ($kind in @([DateTimeKind]::Unspecified, [DateTimeKind]::Local)) {
        Invoke-Test "non-UTC DateTime timestamp is rejected: $kind" {
            $source = Resolve-OpenClawSource $policy
            $source.resolvedAt = [DateTime]::new(2000, 1, 1, 0, 0, 0, $kind)
            Assert-TestThrows { Assert-OpenClawSource $source $policy } 'resolvedAt'
        }
    }

    foreach ($case in @(
            @{ Field = 'repository'; Value = 'https://github.com/other/openclaw'; Error = 'repository' },
            @{ Field = 'requestedRef'; Value = 'main'; Error = 'requestedRef' },
            @{ Field = 'requestedRef'; Value = "stable`r`nevil=value"; Error = 'requestedRef' },
            @{ Field = 'resolvedCommit'; Value = $commit.ToUpperInvariant(); Error = 'resolvedCommit' },
            @{ Field = 'resolvedCommit'; Value = @($commit); Error = 'resolvedCommit' },
            @{ Field = 'packageVersion'; Value = '2026.9.4-beta.1'; Error = 'packageVersion' },
            @{ Field = 'channel'; Value = 'latest'; Error = 'channel' },
            @{ Field = 'channel'; Value = ''; Error = 'ref override' },
            @{ Field = 'releaseTag'; Value = 'v2026.9.3'; Error = 'releaseTag' },
            @{ Field = 'tagObject'; Value = ''; Error = 'tagObject' },
            @{ Field = 'tagObject'; Value = $tagObject.ToUpperInvariant(); Error = 'tagObject' },
            @{ Field = 'registryIntegrity'; Value = 'sha512-invalid'; Error = 'registryIntegrity' },
            @{ Field = 'resolvedAt'; Value = '2026-02-30T12:00:00Z'; Error = 'resolvedAt' },
            @{ Field = 'resolvedAt'; Value = 'not-a-date'; Error = 'resolvedAt' },
            @{ Field = 'resolvedAt'; Value = '2026-06-01'; Error = 'resolvedAt' },
            @{ Field = 'resolvedAt'; Value = '2026-06-01T00:00:00'; Error = 'resolvedAt' },
            @{ Field = 'resolvedAt'; Value = "2026-06-01T00:00:00Z`n"; Error = 'resolvedAt' }
        )) {
        Invoke-Test "snapshot identity rejects inconsistent $($case.Field)" {
            $source = Resolve-OpenClawSource $policy
            $source.($case.Field) = $case.Value
            Assert-TestThrows { Assert-OpenClawSource $source $policy } $case.Error
        }
    }

    foreach ($field in @(
            'repository', 'requestedRef', 'resolvedCommit', 'packageVersion',
            'channel', 'releaseTag', 'tagObject', 'resolvedAt', 'registryIntegrity'
        )) {
        Invoke-Test "snapshot requires $field" {
            $source = Resolve-OpenClawSource $policy
            $source.PSObject.Properties.Remove($field)
            Assert-TestThrows { Assert-OpenClawSource $source $policy } $field
        }
    }

    foreach ($revision in @(0, 65534)) {
        Invoke-Test "policy accepts boundary packageRevision $revision" {
            $candidate = New-TestPolicy
            $candidate.packageRevision = $revision
            $read = Read-TestPolicy ($candidate | ConvertTo-Json)
            Assert-TestEqual $read.packageRevision $revision
        }
    }

    foreach ($pin in ($invalidStableVersions + @('', $null, 'stable', 'latest', 'extended-stable'))) {
        Invoke-Test 'policy rejects invalid, extended, empty, and null stableVersion pins' {
            $candidate = New-TestPolicy
            $candidate | Add-Member -NotePropertyName stableVersion -NotePropertyValue $pin
            Assert-TestThrows {
                Read-TestPolicy ($candidate | ConvertTo-Json -Depth 8)
            } 'stableVersion'
            Assert-TestThrows { Get-OpenClawPolicyRef $candidate } 'stableVersion'
            Assert-TestThrows { Resolve-OpenClawSource $candidate } 'stableVersion'
            Assert-TestEqual $requests.Count 0
        }
    }

    foreach ($case in @(
            @{ Field = 'repository'; Value = 'https://github.com/other/openclaw' },
            @{ Field = 'repository'; Value = @('https://github.com/openclaw/openclaw') },
            @{ Field = 'channel'; Value = 'latest' },
            @{ Field = 'channel'; Value = 'Stable' },
            @{ Field = 'channel'; Value = 'extended-stable' },
            @{ Field = 'packageRevision'; Value = -1 },
            @{ Field = 'packageRevision'; Value = 65535 },
            @{ Field = 'packageRevision'; Value = '1' },
            @{ Field = 'packageRevision'; Value = 1.0 },
            @{ Field = 'packageRevision'; Value = $true },
            @{ Field = 'packageRevision'; Value = $null },
            @{ Field = 'publisher'; Value = '' },
            @{ Field = 'publisher'; Value = '   ' },
            @{ Field = 'publisher'; Value = "CN=Test`nINJECT=value" }
        )) {
        Invoke-Test "policy rejects malformed $($case.Field)" {
            $candidate = New-TestPolicy
            $candidate.($case.Field) = $case.Value
            Assert-TestThrows {
                Read-TestPolicy ($candidate | ConvertTo-Json -Depth 8)
            } $case.Field
        }
    }

    foreach ($field in @('repository', 'channel', 'packageRevision', 'publisher')) {
        Invoke-Test "policy requires $field" {
            $candidate = New-TestPolicy
            $candidate.PSObject.Properties.Remove($field)
            Assert-TestThrows { Read-TestPolicy ($candidate | ConvertTo-Json) } $field
        }
    }

    foreach ($json in @('[]', '[{"repository":"ignored"}]', 'null', '"string"', '{')) {
        Invoke-Test 'policy rejects nonobjects and invalid JSON' {
            Assert-TestThrows { Read-TestPolicy $json } 'object|JSON'
        }
    }

    foreach ($case in @(
            @{ Version = '2026.9.4'; Revision = 0; Expected = '2026.9.4.0' },
            @{ Version = '2026.9.4'; Revision = 1; Expected = '2026.9.4.1' },
            @{ Version = '2026.9.4'; Revision = [long]65534; Expected = '2026.9.4.65534' },
            @{ Version = '2026.9.4-1'; Revision = 0; Expected = '2026.9.4.1' },
            @{ Version = '2026.9.4-2'; Revision = 3; Expected = '2026.9.4.5' },
            @{ Version = '2026.9.4-65534'; Revision = 0; Expected = '2026.9.4.65534' },
            @{ Version = '2026.9.4-65533'; Revision = 1; Expected = '2026.9.4.65534' },
            @{ Version = '9999.12.32'; Revision = 0; Expected = '9999.12.32.0' },
            @{ Version = '2026.1.1'; Revision = 0; Expected = '2026.1.1.0' }
        )) {
        Invoke-Test "MSIX mapping preserves base and adds numeric corrections: $($case.Expected)" {
            $results = @(Get-OpenClawMsixReleaseVersion -Version $case.Version -PackageRevision $case.Revision)
            Assert-TestEqual $results.Count 1
            Assert-TestEqual ($results[0] -is [string]) $true
            Assert-TestEqual $results[0] $case.Expected
        }
    }

    foreach ($case in @(
            @{ Version = '2026.9.4-65535'; Revision = 0 },
            @{ Version = '2026.9.4-65534'; Revision = 1 },
            @{ Version = '2026.9.4-1'; Revision = 65534 },
            @{ Version = '2026.9.4-2147483647'; Revision = 0 },
            @{ Version = '2026.9.4-2147483648'; Revision = 0 },
            @{ Version = ('2026.9.4-' + ('9' * 200)); Revision = 0 }
        )) {
        Invoke-Test 'MSIX correction overflow is rejected before addition' {
            Assert-TestThrows {
                Get-OpenClawMsixReleaseVersion -Version $case.Version -PackageRevision $case.Revision
            } 'correction.*exceeds 65534'
        }
    }

    foreach ($revision in @(-1, 65535, '1', 1.0, $true, $null, [long]::MaxValue)) {
        Invoke-Test 'MSIX mapping requires an in-range integer package revision' {
            Assert-TestThrows {
                Get-OpenClawMsixReleaseVersion -Version $version -PackageRevision $revision
            } 'packageRevision'
        }
    }

    foreach ($badVersion in $invalidStableVersions) {
        Invoke-Test 'MSIX mapping shares regular stable version validation' {
            Assert-TestThrows {
                Get-OpenClawMsixReleaseVersion -Version $badVersion -PackageRevision 0
            } 'packageVersion'
        }
    }

    Invoke-Test 'HTTP transports pin origins, bound timeouts, and isolate GitHub credentials' {
        $originalToken = $env:GH_TOKEN
        try {
            $env:GH_TOKEN = 'offline-test-token'
            & $githubTransport -Path "git/tags/$tagObject" | Out-Null
            & $registryTransport -Selector 'latest' | Out-Null
            & $registryTransport -Selector $version | Out-Null
            Assert-TestEqual $httpCalls[0].Uri "https://api.github.com/repos/openclaw/openclaw/git/tags/$tagObject"
            Assert-TestEqual $httpCalls[0].Headers.Authorization 'Bearer offline-test-token'
            Assert-TestEqual $httpCalls[1].Uri 'https://registry.npmjs.org/openclaw/latest'
            Assert-TestEqual $httpCalls[2].Uri "https://registry.npmjs.org/openclaw/$version"
            foreach ($call in $httpCalls) {
                Assert-TestEqual $call.TimeoutSec 30
                if ($PSVersionTable.PSVersion -ge [version]'7.4') {
                    Assert-TestEqual $call.OperationTimeoutSeconds 30
                }
                Assert-TestEqual $call.MaximumRedirection 0
                Assert-TestEqual $call.ErrorAction 'Stop'
                if ($call.Uri.StartsWith('https://registry.npmjs.org/')) {
                    Assert-TestEqual $call.Headers.ContainsKey('Authorization') $false
                }
            }
            $env:GH_TOKEN = $null
            & $githubTransport -Path "git/tags/$tagObject" | Out-Null
            Assert-TestEqual $httpCalls[3].Headers.ContainsKey('Authorization') $false
        }
        finally {
            $env:GH_TOKEN = $originalToken
        }
    }

    Invoke-Test 'service helpers reject arbitrary URLs and invalid selectors before transport' {
        Assert-TestThrows {
            & $githubTransport -Path 'https://untrusted.example.invalid'
        } 'GitHub API path'
        Assert-TestThrows { & $registryTransport -Selector '../../other' } 'packageVersion'
        Assert-TestEqual $httpCalls.Count 0
    }

    Invoke-Test 'HTTP helpers accept stable numeric correction manifests and annotated tag refs' {
        & $registryTransport -Selector '2026.9.4-2' | Out-Null
        & $githubTransport -Path 'git/ref/tags/v2026.9.4-2' | Out-Null
        Assert-TestEqual $httpCalls[0].Uri 'https://registry.npmjs.org/openclaw/2026.9.4-2'
        Assert-TestEqual $httpCalls[1].Uri 'https://api.github.com/repos/openclaw/openclaw/git/ref/tags/v2026.9.4-2'
    }

    foreach ($selector in @('stable', 'extended-stable', 'extended-stable/2026.9', 'beta', 'LATEST')) {
        Invoke-Test 'registry transport rejects logical and non-stable channel names' {
            Assert-TestThrows { & $registryTransport -Selector $selector } 'packageVersion'
            Assert-TestEqual $httpCalls.Count 0
        }
    }

    foreach ($badVersion in ($invalidStableVersions | Where-Object { $_ -is [string] })) {
        Invoke-Test 'HTTP manifest and release tag endpoints reject non-stable versions' {
            Assert-TestThrows { & $registryTransport -Selector $badVersion } 'packageVersion'
            Assert-TestThrows { & $githubTransport -Path "git/ref/tags/v$badVersion" } 'packageVersion|GitHub API path'
            Assert-TestEqual $httpCalls.Count 0
        }
    }

    Write-Host "Passed $testCount OpenClaw source resolver tests (offline)."
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
