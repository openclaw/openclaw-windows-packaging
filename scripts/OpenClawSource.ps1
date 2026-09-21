function Get-OpenClawSourceField {
    param([AllowNull()][object]$InputObject, [string]$Name, [switch]$Optional)

    if ($InputObject -is [Collections.IDictionary]) {
        $exists = $InputObject.Contains($Name)
        $value = $InputObject[$Name]
    }
    elseif ($InputObject -is [pscustomobject]) {
        $property = $InputObject.PSObject.Properties[$Name]
        $exists = $null -ne $property
        $value = $null
        if ($exists) { $value = $property.Value }
    }
    else { throw "Expected an object containing '$Name'." }
    if (-not $exists -and $Optional) { return $null }
    if (-not $exists -or $null -eq $value) { throw "Missing required field '$Name'." }
    return ,$value
}

function Assert-OpenClawSourceText {
    param([AllowNull()][object]$Value, [string]$Name, [string]$Pattern = '', [switch]$AllowEmpty)

    if ($Value -isnot [string] -or $Value -match '[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]' -or
        ([string]::IsNullOrWhiteSpace($Value) -and -not ($AllowEmpty -and $Value.Length -eq 0))) {
        throw "'$Name' must be text without control characters or blank whitespace."
    }
    if ($Pattern -and $Value -cnotmatch $Pattern) { throw "'$Name' has an invalid format." }
}

function Assert-OpenClawSourceVersion {
    param([AllowNull()][object]$Version)

    # Patch 33+ is extended stable; numeric suffixes are regular stable corrections.
    Assert-OpenClawSourceText $Version 'packageVersion' -Pattern (
        '\A[1-9][0-9]{3}\.(?:[1-9]|1[0-2])\.(?:[1-9]|[12][0-9]|3[0-2])(?:-[1-9][0-9]*)?\z')
    # Keep package-version limits owned by the release identity helper.
    $null = & (Join-Path $PSScriptRoot 'Get-MSIXReleaseIdentity.ps1') `
        -GatewayTag "v$Version" -MSIXRevision 0
}

function Assert-OpenClawSourceRef {
    param([AllowNull()][object]$Ref)

    Assert-OpenClawSourceText $Ref 'requestedRef' -Pattern '\A\S+\z'
    if ($Ref -in @('.', '..') -or $Ref -match '\A(?:refs/(?:heads|tags)/)?extended-stable(?:/|\z)') {
        throw 'The requestedRef cannot select extended-stable or a relative path.'
    }
}

function Get-OpenClawPolicyRef {
    param([Parameter(Mandatory)][object]$Policy)

    $repository = Get-OpenClawSourceField $Policy 'repository'
    Assert-OpenClawSourceText $repository 'repository'
    if ($repository -cne 'https://github.com/openclaw/openclaw') {
        throw 'The release policy repository must be https://github.com/openclaw/openclaw.'
    }
    $channel = Get-OpenClawSourceField $Policy 'channel' -Optional
    if ($null -ne $channel -and ($channel -isnot [string] -or $channel -cne 'stable')) {
        throw 'The release policy channel must be stable when specified.'
    }
    $pin = Get-OpenClawSourceField $Policy 'stableVersion' -Optional
    if ($null -ne $pin) {
        Assert-OpenClawSourceVersion $pin
        return $pin
    }
    return 'stable'
}

function Read-OpenClawReleasePolicy {
    param([Parameter(Mandatory)][string]$Path)

    $policy = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop |
        ConvertFrom-Json -Depth 16 -NoEnumerate -ErrorAction Stop
    $null = Get-OpenClawPolicyRef $policy
    return $policy
}

function Assert-OpenClawRegistryIntegrity {
    param([AllowNull()][object]$Integrity)

    Assert-OpenClawSourceText $Integrity 'registryIntegrity' -Pattern '\Asha512-[A-Za-z0-9+/]{86}==\z'
    $encoded = $Integrity.Substring(7)
    if ([Convert]::ToBase64String([Convert]::FromBase64String($encoded)) -cne $encoded) {
        throw 'registryIntegrity must contain a canonical SHA-512 digest.'
    }
}

function Invoke-OpenClawSourceRequest {
    param([ValidateSet('GitHub', 'Registry')][string]$Service, [string]$Path)

    $options = @{ TimeoutSec = 30; MaximumRedirection = 0; ErrorAction = 'Stop' }
    # PowerShell 7.4+ separates connection and response timeouts.
    if ($PSVersionTable.PSVersion -ge [version]'7.4') { $options.OperationTimeoutSeconds = 30 }
    $headers = @{ Accept = 'application/json' }
    if ($Service -eq 'GitHub') {
        $origin = 'https://api.github.com/repos/openclaw/openclaw/'
        $headers.Accept = 'application/vnd.github+json'
        $headers['User-Agent'] = 'OpenClaw-Gateway-MSIX'
        $headers['X-GitHub-Api-Version'] = '2022-11-28'
        if (-not [string]::IsNullOrEmpty($env:GH_TOKEN)) {
            Assert-OpenClawSourceText $env:GH_TOKEN 'GH_TOKEN'
            $headers.Authorization = "Bearer $env:GH_TOKEN"
        }
    }
    else { $origin = 'https://registry.npmjs.org/openclaw/' }
    # Paths are constructed locally; never follow response URLs or HTTP redirects.
    return Invoke-RestMethod -Uri "$origin$Path" -Headers $headers @options
}

function Get-OpenClawCommitPackageVersion {
    param([string]$Commit)

    $file = Invoke-OpenClawSourceRequest GitHub "contents/package.json?ref=$Commit"
    if ($file.type -cne 'file' -or $file.encoding -cne 'base64' -or $file.content -isnot [string]) {
        throw 'GitHub must return package.json as a base64-encoded file.'
    }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $package = $utf8.GetString([Convert]::FromBase64String($file.content)) |
        ConvertFrom-Json -Depth 16 -NoEnumerate -ErrorAction Stop
    if ($package.name -isnot [string] -or $package.name -cne 'openclaw') {
        throw 'The source package name must be openclaw.'
    }
    Assert-OpenClawSourceVersion $package.version
    return $package.version
}

function Resolve-OpenClawSource {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Policy, [AllowEmptyString()][string]$Ref = '')

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $policyRef = Get-OpenClawPolicyRef $Policy
    Assert-OpenClawSourceText $Ref 'Ref' -AllowEmpty
    $channel = $releaseTag = $tagObject = $integrity = ''
    if ($Ref.Length -gt 0) {
        Assert-OpenClawSourceRef $Ref
        $requestedRef = $Ref
        $response = Invoke-OpenClawSourceRequest GitHub "commits/$([Uri]::EscapeDataString($Ref))"
        Assert-OpenClawSourceText $response.sha 'commit' -Pattern '\A[0-9a-fA-F]{40}\z'
        $commit = $response.sha.ToLowerInvariant()
        if ($Ref -match '\A[0-9a-fA-F]{40}\z' -and $Ref -ine $commit) {
            throw 'The resolved commit does not match the full SHA override.'
        }
        $version = Get-OpenClawCommitPackageVersion $commit
    }
    else {
        $channel = 'stable'
        $requestedRef = $policyRef
        $selector = if ($policyRef -ceq 'stable') { 'latest' } else { $policyRef }
        $selection = Invoke-OpenClawSourceRequest Registry $selector
        if ($selection.name -isnot [string] -or $selection.name -cne 'openclaw') {
            throw 'The registry selector must resolve to the openclaw package.'
        }
        $version = $selection.version
        Assert-OpenClawSourceVersion $version
        if ($policyRef -cne 'stable' -and $version -cne $policyRef) {
            throw 'The registry version does not match the stableVersion pin.'
        }
        $manifest = $selection
        if ($policyRef -ceq 'stable') { $manifest = Invoke-OpenClawSourceRequest Registry $version }
        if ($manifest.name -isnot [string] -or $manifest.name -cne 'openclaw' -or
            $manifest.version -isnot [string] -or $manifest.version -cne $version) {
            throw 'The exact registry manifest does not match the selected package version.'
        }
        $repository = $manifest.repository
        if ($repository -isnot [string]) { $repository = Get-OpenClawSourceField $repository 'url' }
        Assert-OpenClawSourceText $repository 'registry repository'
        if ($repository -cnotin @(
                'https://github.com/openclaw/openclaw', 'git+https://github.com/openclaw/openclaw.git')) {
            throw 'The registry package repository does not match the release policy.'
        }
        $integrity = $manifest.dist.integrity
        Assert-OpenClawRegistryIntegrity $integrity
        $releaseTag = "v$version"
        $tagRef = Invoke-OpenClawSourceRequest GitHub "git/ref/tags/$releaseTag"
        if ($tagRef.ref -isnot [string] -or $tagRef.ref -cne "refs/tags/$releaseTag" -or
            $tagRef.object.type -isnot [string] -or $tagRef.object.type -cne 'tag') {
            throw 'The selected release must have an exact annotated tag ref.'
        }
        Assert-OpenClawSourceText $tagRef.object.sha 'tag object' -Pattern '\A[0-9a-fA-F]{40}\z'
        $tagObject = $tagRef.object.sha.ToLowerInvariant()
        $tag = Invoke-OpenClawSourceRequest GitHub "git/tags/$tagObject"
        Assert-OpenClawSourceText $tag.sha 'tag SHA' -Pattern '\A[0-9a-fA-F]{40}\z'
        if ($tag.sha -ine $tagObject -or $tag.tag -isnot [string] -or $tag.tag -cne $releaseTag -or
            $tag.verification.verified -isnot [bool] -or -not $tag.verification.verified -or
            $tag.verification.reason -isnot [string] -or $tag.verification.reason -cne 'valid') {
            throw 'The release tag must match and have a valid GitHub-verified signature.'
        }
        if ($tag.object.type -isnot [string] -or $tag.object.type -cne 'commit') {
            throw 'The annotated release tag must point directly to a commit.'
        }
        Assert-OpenClawSourceText $tag.object.sha 'commit' -Pattern '\A[0-9a-fA-F]{40}\z'
        $commit = $tag.object.sha.ToLowerInvariant()
        if ((Get-OpenClawCommitPackageVersion $commit) -cne $version) {
            throw 'The source package version does not match the selected registry version.'
        }
        $gitHead = Get-OpenClawSourceField $manifest 'gitHead' -Optional
        if ($null -ne $gitHead) {
            Assert-OpenClawSourceText $gitHead 'registry gitHead' -Pattern '\A[0-9a-fA-F]{40}\z'
            if ($gitHead -ine $commit) { throw 'The registry gitHead does not match the release tag commit.' }
        }
    }
    $source = [pscustomobject][ordered]@{
        repository = Get-OpenClawSourceField $Policy 'repository'
        requestedRef = $requestedRef
        resolvedCommit = $commit
        packageVersion = $version
        channel = $channel
        releaseTag = $releaseTag
        tagObject = $tagObject
        resolvedAt = [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
        registryIntegrity = $integrity
    }
    Assert-OpenClawSource $source $Policy
    return $source
}

function Assert-OpenClawSource {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Source, [Parameter(Mandatory)][object]$Policy, [switch]$RequireChannel)

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $policyRef = Get-OpenClawPolicyRef $Policy
    $values = @{}
    foreach ($field in @(
            'repository', 'requestedRef', 'resolvedCommit', 'packageVersion',
            'channel', 'releaseTag', 'tagObject', 'resolvedAt', 'registryIntegrity')) {
        $value = Get-OpenClawSourceField $Source $field
        # ConvertFrom-Json can materialize UTC timestamps as DateTime.
        if ($field -eq 'resolvedAt' -and $value -is [DateTime] -and $value.Kind -eq [DateTimeKind]::Utc) {
            $value = $value.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
        }
        Assert-OpenClawSourceText $value $field -AllowEmpty:(
            $field -in @('channel', 'releaseTag', 'tagObject', 'registryIntegrity'))
        $values[$field] = $value
    }
    if ($values.repository -cne (Get-OpenClawSourceField $Policy 'repository')) {
        throw 'The source repository does not match the release policy.'
    }
    Assert-OpenClawSourceRef $values.requestedRef
    Assert-OpenClawSourceText $values.resolvedCommit 'resolvedCommit' -Pattern '\A[0-9a-f]{40}\z'
    Assert-OpenClawSourceVersion $values.packageVersion
    Assert-OpenClawSourceText $values.resolvedAt 'resolvedAt' -Pattern (
        '\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|\+00:00)\z')
    $timestamp = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($values.resolvedAt, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None, [ref]$timestamp)) {
        throw 'resolvedAt must be a valid UTC RFC3339 timestamp.'
    }
    if ($values.channel.Length -gt 0) {
        if ($values.channel -cne 'stable' -or $values.requestedRef -cne $policyRef) {
            throw 'The source channel and requestedRef must match the release policy.'
        }
        if ($policyRef -cne 'stable' -and $values.packageVersion -cne $policyRef) {
            throw 'The source packageVersion must match the stableVersion pin.'
        }
        if ($values.releaseTag -cne "v$($values.packageVersion)") {
            throw 'The releaseTag must match the source package version.'
        }
        Assert-OpenClawSourceText $values.tagObject 'tagObject' -Pattern '\A[0-9a-f]{40}\z'
        Assert-OpenClawRegistryIntegrity $values.registryIntegrity
    }
    else {
        if ($RequireChannel) { throw 'A channel-resolved source is required, not a ref override.' }
        if ($values.releaseTag -cne '' -or $values.tagObject -cne '' -or $values.registryIntegrity -cne '') {
            throw 'A ref override must have empty releaseTag, tagObject, and registryIntegrity fields.'
        }
        if ($values.requestedRef -match '\A[0-9a-fA-F]{40}\z' -and
            $values.requestedRef -ine $values.resolvedCommit) {
            throw 'The resolved commit does not match the full SHA override.'
        }
    }
}
