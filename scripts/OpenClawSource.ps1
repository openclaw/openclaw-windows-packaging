function Get-OpenClawSourceField {
    param(
        [AllowNull()]
        [object]$InputObject,
        [string]$Name,
        [switch]$Optional
    )

    if ($InputObject -is [System.Collections.IDictionary]) {
        $exists = $InputObject.Contains($Name)
        $value = $InputObject[$Name]
    }
    elseif ($InputObject -is [System.Management.Automation.PSCustomObject]) {
        $property = $InputObject.PSObject.Properties[$Name]
        $exists = $null -ne $property
        $value = $null
        if ($exists) {
            $value = $property.Value
        }
    }
    else {
        throw "Expected an object containing '$Name'."
    }

    if (-not $exists -and $Optional) {
        return $null
    }
    if (-not $exists -or $null -eq $value) {
        throw "Missing required field '$Name'."
    }

    return ,$value
}

function Assert-OpenClawSourceText {
    param(
        [AllowNull()]
        [object]$Value,
        [string]$Name,
        [string]$Pattern = '',
        [switch]$AllowEmpty
    )

    if ($Value -isnot [string] -or
        $Value -match '[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]' -or
        ([string]::IsNullOrWhiteSpace($Value) -and
            -not ($AllowEmpty -and $Value.Length -eq 0))) {
        throw "'$Name' must be a string without control characters or blank whitespace."
    }
    if ($Pattern -and $Value -cnotmatch $Pattern) {
        throw "'$Name' has an invalid format."
    }
}

function Get-OpenClawStableVersionMatch {
    param(
        [AllowNull()]
        [object]$Version,
        [string]$Name = 'packageVersion'
    )

    Assert-OpenClawSourceText $Version $Name
    # Upstream reserves patch 33+ for extended stable; numeric suffixes are stable corrections.
    $match = [regex]::Match(
        $Version,
        '\A(?<year>[1-9][0-9]{3})\.(?<month>[1-9]|1[0-2])\.' +
        '(?<patch>[1-9]|[12][0-9]|3[0-2])(?:-(?<correction>[1-9][0-9]*))?\z'
    )
    if (-not $match.Success) {
        throw "'$Name' must be a regular stable version (YYYY.M.P or YYYY.M.P-C)."
    }
    return $match
}

function Assert-OpenClawSourceVersion {
    param(
        [AllowNull()]
        [object]$Version,
        [switch]$Final
    )

    # Retain -Final for packaging callers; both modes now require regular stable.
    $null = Get-OpenClawStableVersionMatch -Version $Version
}

function Assert-OpenClawSourceRef {
    param(
        [AllowNull()]
        [object]$Ref,
        [string]$Name = 'Ref'
    )

    Assert-OpenClawSourceText $Ref $Name -Pattern '\A\S+\z'
    if ($Ref -match '\A(?:refs/(?:heads|tags)/)?extended-stable(?:/|\z)') {
        throw "'$Name' cannot select extended-stable."
    }
}

function Assert-OpenClawPackageRevision {
    param(
        [AllowNull()]
        [object]$PackageRevision
    )

    if (($PackageRevision -isnot [long] -and $PackageRevision -isnot [int]) -or
        $PackageRevision -lt 0 -or $PackageRevision -gt 65534) {
        throw 'packageRevision must be a JSON integer between 0 and 65534.'
    }
}

function Get-OpenClawMsixReleaseVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Version,
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$PackageRevision
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    $match = Get-OpenClawStableVersionMatch -Version $Version
    Assert-OpenClawPackageRevision -PackageRevision $PackageRevision
    $correction = 0
    if ($match.Groups['correction'].Success -and (
            -not [int]::TryParse(
                $match.Groups['correction'].Value,
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$correction
            ) -or $correction -gt (65534 - $PackageRevision)
        )) {
        throw 'The stable correction plus packageRevision exceeds 65534.'
    }
    $revision = $correction + $PackageRevision
    return '{0}.{1}.{2}.{3}' -f $match.Groups['year'].Value,
        $match.Groups['month'].Value, $match.Groups['patch'].Value, $revision
}

function Assert-OpenClawRegistryIntegrity {
    param(
        [AllowNull()]
        [object]$Integrity
    )

    Assert-OpenClawSourceText `
        -Value $Integrity `
        -Name 'registryIntegrity' `
        -Pattern '\Asha512-[A-Za-z0-9+/]{86}==\z'
    $encoded = $Integrity.Substring(7)
    $bytes = [Convert]::FromBase64String($encoded)
    if ($bytes.Length -ne 64 -or
        [Convert]::ToBase64String($bytes) -cne $encoded) {
        throw 'registryIntegrity must contain a canonical SHA-512 digest.'
    }
}

function Assert-OpenClawReleasePolicy {
    param(
        [AllowNull()]
        [object]$Policy
    )

    $repository = Get-OpenClawSourceField $Policy 'repository'
    $channel = Get-OpenClawSourceField $Policy 'channel'
    $revision = Get-OpenClawSourceField $Policy 'packageRevision'
    $publisher = Get-OpenClawSourceField $Policy 'publisher'
    Assert-OpenClawSourceText $repository 'repository'
    Assert-OpenClawSourceText $channel 'channel'
    Assert-OpenClawSourceText $publisher 'publisher'
    if ($repository -cne 'https://github.com/openclaw/openclaw') {
        throw 'The release policy repository must be https://github.com/openclaw/openclaw.'
    }
    if ($channel -cne 'stable') {
        throw 'The release policy channel must be stable.'
    }
    Assert-OpenClawPackageRevision -PackageRevision $revision
    $pin = Get-OpenClawSourceField $Policy 'stableVersion' -Optional
    if ($null -ne $pin) {
        $null = Get-OpenClawStableVersionMatch -Version $pin -Name 'stableVersion'
    }
}

function Get-OpenClawPolicyRef {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Policy
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    Assert-OpenClawReleasePolicy -Policy $Policy
    $pin = Get-OpenClawSourceField $Policy 'stableVersion' -Optional
    if ($null -ne $pin) {
        return $pin
    }
    return 'stable'
}

function Read-OpenClawReleasePolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    $policy = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 32 -NoEnumerate
    Assert-OpenClawReleasePolicy -Policy $policy
    return $policy
}

function Get-OpenClawRequestOptions {
    $options = @{
        TimeoutSec = 30
        MaximumRedirection = 0
        ErrorAction = 'Stop'
    }
    # Since PowerShell 7.4, TimeoutSec only bounds connection establishment.
    if ($PSVersionTable.PSVersion -ge [version]'7.4') {
        $options.OperationTimeoutSeconds = 30
    }
    return $options
}

function Invoke-OpenClawGitHubRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    $segment = '(?:[A-Za-z0-9._~-]|%[0-9A-Fa-f]{2})+'
    $allowedPath = '\A(?:commits/' + $segment +
        '|git/tags/[0-9a-f]{40}|contents/package\.json\?ref=[0-9a-f]{40})\z'
    Assert-OpenClawSourceText $Path 'GitHub API path'
    $tagPrefix = 'git/ref/tags/v'
    if ($Path.StartsWith($tagPrefix, [StringComparison]::Ordinal)) {
        Assert-OpenClawSourceVersion -Version $Path.Substring($tagPrefix.Length)
    }
    else {
        Assert-OpenClawSourceText $Path 'GitHub API path' -Pattern $allowedPath
    }
    $headers = @{
        Accept = 'application/vnd.github+json'
        'User-Agent' = 'OpenClaw-Gateway-MSIX'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    if (-not [string]::IsNullOrEmpty($env:GH_TOKEN)) {
        Assert-OpenClawSourceText $env:GH_TOKEN 'GH_TOKEN'
        $headers.Authorization = "Bearer $env:GH_TOKEN"
    }

    # Refuse redirects so credentials never leave the fixed GitHub API origin.
    $requestOptions = Get-OpenClawRequestOptions
    return Invoke-RestMethod `
        -Uri "https://api.github.com/repos/openclaw/openclaw/$Path" `
        -Headers $headers `
        @requestOptions
}

function Invoke-OpenClawRegistryRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Selector
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    if ($Selector -cne 'latest') {
        Assert-OpenClawSourceVersion -Version $Selector
    }
    $encodedSelector = [Uri]::EscapeDataString($Selector)
    $requestOptions = Get-OpenClawRequestOptions
    return Invoke-RestMethod `
        -Uri "https://registry.npmjs.org/openclaw/$encodedSelector" `
        -Headers @{ Accept = 'application/json' } `
        @requestOptions
}

function Get-OpenClawCommitPackageVersion {
    param(
        [string]$Commit
    )

    Assert-OpenClawSourceText $Commit 'commit' -Pattern '\A[0-9a-f]{40}\z'
    $file = Invoke-OpenClawGitHubRequest -Path "contents/package.json?ref=$Commit"
    $fileType = Get-OpenClawSourceField $file 'type'
    $encoding = Get-OpenClawSourceField $file 'encoding'
    Assert-OpenClawSourceText $fileType 'package.json type'
    Assert-OpenClawSourceText $encoding 'package.json encoding'
    if ($fileType -cne 'file' -or $encoding -cne 'base64') {
        throw 'GitHub must return package.json as a base64-encoded file.'
    }
    $content = Get-OpenClawSourceField $file 'content'
    if ($content -isnot [string]) {
        throw 'The package.json content must be base64 text.'
    }
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $json = $utf8.GetString([Convert]::FromBase64String($content))
    $package = ConvertFrom-Json -InputObject $json -Depth 32 -NoEnumerate
    $name = Get-OpenClawSourceField $package 'name'
    Assert-OpenClawSourceText $name 'package name'
    if ($name -cne 'openclaw') {
        throw 'The source package name must be openclaw.'
    }
    $version = Get-OpenClawSourceField $package 'version'
    Assert-OpenClawSourceVersion -Version $version
    return $version
}

function Resolve-OpenClawSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Policy,
        [AllowEmptyString()]
        [string]$Ref = ''
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    $policyRef = Get-OpenClawPolicyRef -Policy $Policy
    Assert-OpenClawSourceText $Ref 'Ref' -AllowEmpty
    $channel = ''
    $releaseTag = ''
    $tagObject = ''
    $integrity = ''

    if ($Ref.Length -gt 0) {
        Assert-OpenClawSourceRef -Ref $Ref
        $requestedRef = $Ref
        $escapedRef = [Uri]::EscapeDataString($Ref)
        $commitResponse = Invoke-OpenClawGitHubRequest -Path "commits/$escapedRef"
        $commit = Get-OpenClawSourceField $commitResponse 'sha'
        Assert-OpenClawSourceText $commit 'commit' -Pattern '\A[0-9a-fA-F]{40}\z'
        $commit = $commit.ToLowerInvariant()
        $version = Get-OpenClawCommitPackageVersion -Commit $commit
    }
    else {
        $channel = Get-OpenClawSourceField $Policy 'channel'
        $requestedRef = $policyRef
        $selector = if ($policyRef -ceq 'stable') { 'latest' } else { $policyRef }
        $selection = Invoke-OpenClawRegistryRequest -Selector $selector
        $name = Get-OpenClawSourceField $selection 'name'
        Assert-OpenClawSourceText $name 'registry package name'
        if ($name -cne 'openclaw') {
            throw 'The registry channel must resolve to the openclaw package.'
        }
        $version = Get-OpenClawSourceField $selection 'version'
        Assert-OpenClawSourceVersion -Version $version
        if ($policyRef -cne 'stable' -and $version -cne $policyRef) {
            throw 'The registry package version does not match the stableVersion pin.'
        }
        $releaseTag = "v$version"
        $tagRef = Invoke-OpenClawGitHubRequest -Path "git/ref/tags/$releaseTag"
        $refLabel = Get-OpenClawSourceField $tagRef 'ref'
        Assert-OpenClawSourceText $refLabel 'tag ref'
        $refObject = Get-OpenClawSourceField $tagRef 'object'
        $refType = Get-OpenClawSourceField $refObject 'type'
        Assert-OpenClawSourceText $refType 'tag ref type'
        if ($refLabel -cne "refs/tags/$releaseTag" -or $refType -cne 'tag') {
            throw 'The selected release must have an exact annotated tag ref.'
        }
        $tagObject = Get-OpenClawSourceField $refObject 'sha'
        Assert-OpenClawSourceText $tagObject 'tag object' -Pattern '\A[0-9a-fA-F]{40}\z'
        $tagObject = $tagObject.ToLowerInvariant()
        $tag = Invoke-OpenClawGitHubRequest -Path "git/tags/$tagObject"
        $tagSha = Get-OpenClawSourceField $tag 'sha'
        Assert-OpenClawSourceText $tagSha 'tag SHA' -Pattern '\A[0-9a-fA-F]{40}\z'
        $tagLabel = Get-OpenClawSourceField $tag 'tag'
        Assert-OpenClawSourceText $tagLabel 'tag label'
        $verification = Get-OpenClawSourceField $tag 'verification'
        $verified = Get-OpenClawSourceField $verification 'verified'
        $reason = Get-OpenClawSourceField $verification 'reason'
        Assert-OpenClawSourceText $reason 'tag verification reason'
        if ($tagSha.ToLowerInvariant() -cne $tagObject -or
            $tagLabel -cne $releaseTag -or
            $verified -isnot [bool] -or -not $verified -or $reason -cne 'valid') {
            throw 'The release tag must match and have a valid GitHub-verified signature.'
        }
        $target = Get-OpenClawSourceField $tag 'object'
        $targetType = Get-OpenClawSourceField $target 'type'
        Assert-OpenClawSourceText $targetType 'tag target type'
        if ($targetType -cne 'commit') {
            throw 'The annotated release tag must point directly to a commit.'
        }
        $commit = Get-OpenClawSourceField $target 'sha'
        Assert-OpenClawSourceText $commit 'commit' -Pattern '\A[0-9a-fA-F]{40}\z'
        $commit = $commit.ToLowerInvariant()
        if ((Get-OpenClawCommitPackageVersion -Commit $commit) -cne $version) {
            throw 'The immutable source package version does not match the selected release.'
        }

        # A pin already fetched the exact manifest; latest needs an immutable version lookup.
        $manifest = $selection
        if ($policyRef -ceq 'stable') {
            $manifest = Invoke-OpenClawRegistryRequest -Selector $version
        }
        $manifestName = Get-OpenClawSourceField $manifest 'name'
        $manifestVersion = Get-OpenClawSourceField $manifest 'version'
        Assert-OpenClawSourceText $manifestName 'registry package name'
        Assert-OpenClawSourceVersion -Version $manifestVersion
        if ($manifestName -cne 'openclaw' -or $manifestVersion -cne $version) {
            throw 'The exact registry manifest does not match the selected package version.'
        }
        $repository = Get-OpenClawSourceField $manifest 'repository'
        if ($repository -isnot [string]) {
            $repository = Get-OpenClawSourceField $repository 'url'
        }
        Assert-OpenClawSourceText $repository 'registry repository'
        if ($repository -cnotin @(
                'https://github.com/openclaw/openclaw',
                'git+https://github.com/openclaw/openclaw.git'
            )) {
            throw 'The registry package repository does not match the release policy.'
        }
        $dist = Get-OpenClawSourceField $manifest 'dist'
        $integrity = Get-OpenClawSourceField $dist 'integrity'
        Assert-OpenClawRegistryIntegrity -Integrity $integrity
        $gitHead = Get-OpenClawSourceField $manifest 'gitHead' -Optional
        if ($null -ne $gitHead) {
            Assert-OpenClawSourceText $gitHead 'registry gitHead' -Pattern '\A[0-9a-fA-F]{40}\z'
            if ($gitHead.ToLowerInvariant() -cne $commit) {
                throw 'The registry gitHead does not match the release tag commit.'
            }
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
    Assert-OpenClawSource -Source $source -Policy $Policy
    return $source
}

function Assert-OpenClawSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Source,
        [Parameter(Mandatory)]
        [object]$Policy,
        [switch]$RequireChannel
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    $policyRef = Get-OpenClawPolicyRef -Policy $Policy
    $values = @{}
    foreach ($field in @(
            'repository', 'requestedRef', 'resolvedCommit', 'packageVersion',
            'channel', 'releaseTag', 'tagObject', 'resolvedAt', 'registryIntegrity'
        )) {
        $values[$field] = Get-OpenClawSourceField $Source $field
        # ConvertFrom-Json automatically materializes Z timestamps as UTC DateTime.
        if ($field -eq 'resolvedAt' -and $values[$field] -is [DateTime]) {
            if ($values[$field].Kind -ne [DateTimeKind]::Utc) {
                throw 'resolvedAt must be a UTC DateTime or UTC RFC3339 string.'
            }
            $values[$field] = $values[$field].ToString('o', [Globalization.CultureInfo]::InvariantCulture)
        }
        $allowEmpty = $field -in @('channel', 'releaseTag', 'tagObject', 'registryIntegrity')
        Assert-OpenClawSourceText $values[$field] $field -AllowEmpty:$allowEmpty
    }
    if ($values.repository -cne (Get-OpenClawSourceField $Policy 'repository')) {
        throw 'The source repository does not match the release policy.'
    }
    Assert-OpenClawSourceRef $values.requestedRef 'requestedRef'
    Assert-OpenClawSourceText $values.resolvedCommit 'resolvedCommit' -Pattern '\A[0-9a-f]{40}\z'
    Assert-OpenClawSourceVersion $values.packageVersion
    Assert-OpenClawSourceText `
        $values.resolvedAt 'resolvedAt' `
        -Pattern '\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|\+00:00)\z'
    $timestamp = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            $values.resolvedAt,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,
            [ref]$timestamp
        )) {
        throw 'resolvedAt must be a valid UTC RFC3339 timestamp.'
    }

    if ($values.channel.Length -gt 0) {
        $policyChannel = Get-OpenClawSourceField $Policy 'channel'
        if ($values.channel -cne $policyChannel -or $values.requestedRef -cne $policyRef) {
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
        if ($RequireChannel) {
            throw 'Official signing requires a channel-resolved source, not a ref override.'
        }
        if ($values.releaseTag.Length -ne 0 -or
            $values.tagObject.Length -ne 0 -or $values.registryIntegrity.Length -ne 0) {
            throw 'A ref override must have empty releaseTag, tagObject, and registryIntegrity fields.'
        }
    }
}
