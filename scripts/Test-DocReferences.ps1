[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [switch]$Advisory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$IgnoredPathPrefixes = @(
    # README.md references this action from the upstream openclaw/openclaw repository.
    '.github/actions/'
)

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    $output = @(& git -C $RepositoryRoot @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (git exit code $LASTEXITCODE)."
    }

    return $output
}

function Get-NormalizedToken {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Token
    )

    $normalized = $Token.Trim()
    $normalized = $normalized -replace '^[\(\[\{<]+', ''
    $normalized = $normalized -replace '[\.,;:!?\)\]\}>]+$', ''
    $normalized = $normalized -replace '\\', '/'
    while ($normalized.StartsWith('./', [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }
    $normalized = $normalized.TrimEnd('/')
    return $normalized
}

function Test-InScopeToken {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Token
    )

    if ([string]::IsNullOrWhiteSpace($Token) -or
        $Token -match '[%<>*$|]' -or
        $Token -match '://' -or
        $Token -match '^(?:~|/|[A-Za-z]:)') {
        return $false
    }

    foreach ($ignoredPrefix in $IgnoredPathPrefixes) {
        if ($Token.StartsWith($ignoredPrefix, [StringComparison]::Ordinal)) {
            return $false
        }
    }

    # Bare filenames are ambiguous prose or generated-artifact references; only
    # directory-qualified repository paths can be validated without guessing.
    if ($Token -match '^(?:src|tests|scripts|docs|hooks|plugins|\.github)/') {
        return $true
    }

    return $false
}

function Get-HeadingSlugs {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    $slugs = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal
    )
    $counts = @{}
    foreach ($line in $Lines) {
        if ($line -notmatch '^\s{0,3}#{1,6}\s+(?<heading>.*?)\s*#*\s*$') {
            continue
        }

        $slug = $Matches.heading.ToLowerInvariant()
        $slug = $slug -replace '`', ''
        $slug = $slug -replace '[^\p{L}\p{N}\s-]', ''
        $slug = $slug -replace '\s+', '-'
        $slug = $slug.Trim('-')
        if ([string]::IsNullOrEmpty($slug)) {
            continue
        }

        if ($counts.ContainsKey($slug)) {
            $counts[$slug]++
            $slug = "$slug-$($counts[$slug])"
        }
        else {
            $counts[$slug] = 0
        }
        [void]$slugs.Add($slug)
    }

    return ,$slugs
}

function Add-Finding {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Findings,

        [Parameter(Mandatory)]
        [string]$File,

        [Parameter(Mandatory)]
        [int]$Line,

        [Parameter(Mandatory)]
        [string]$Rule,

        [Parameter(Mandatory)]
        [string]$Message
    )

    $Findings.Add([pscustomobject]@{
            File = $File
            Line = $Line
            Rule = $Rule
            Message = $Message
        })
}

if ($null -eq (Get-Command git -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1)) {
    throw 'git is unavailable on PATH.'
}

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$insideWorkTree = [string]::Join(
    "`n",
    [string[]](Invoke-Git -Arguments @('rev-parse', '--is-inside-work-tree') `
        -FailureMessage "Unable to verify git work tree at '$RepositoryRoot'")
).Trim()
if ($insideWorkTree -ne 'true') {
    throw "Path is not a git work tree: $RepositoryRoot"
}

$trackedFiles = @(
    Invoke-Git -Arguments @('ls-files') `
        -FailureMessage 'Unable to enumerate tracked files'
)
$trackedSet = [System.Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal
)
foreach ($trackedFile in $trackedFiles) {
    [void]$trackedSet.Add($trackedFile)
}

$markdownFiles = @(
    Invoke-Git -Arguments @('ls-files', '--', '*.md') `
        -FailureMessage 'Unable to enumerate tracked markdown files'
)
$findings = [System.Collections.Generic.List[object]]::new()
$headingCache = @{}

foreach ($markdownFile in $markdownFiles) {
    $fullPath = Join-Path $RepositoryRoot ($markdownFile -replace '/', [IO.Path]::DirectorySeparatorChar)
    $lines = @(Get-Content -LiteralPath $fullPath)
    $headingCache[$markdownFile] = Get-HeadingSlugs -Lines $lines
    $insideFencedCodeBlock = $false

    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = [string]$lines[$index]
        if ($line -match '^\s*(?:```|~~~)') {
            $insideFencedCodeBlock = -not $insideFencedCodeBlock
            continue
        }

        if ($insideFencedCodeBlock) {
            foreach ($match in [regex]::Matches($line, '(?<token>\S+)')) {
                $token = Get-NormalizedToken -Token $match.Groups['token'].Value
                if ((Test-InScopeToken -Token $token) -and
                    -not $trackedSet.Contains($token) -and
                    -not $trackedFiles.Where({
                            $_.StartsWith("$token/", [StringComparison]::Ordinal)
                        }).Count) {
                    Add-Finding -Findings $findings -File $markdownFile -Line ($index + 1) `
                        -Rule 'tracked-path' -Message "Untracked repository path reference '$token'."
                }
            }
        }

        foreach ($match in [regex]::Matches($line, '(?<!`)`(?<token>[^`]+)`')) {
            $token = Get-NormalizedToken -Token $match.Groups['token'].Value
            if ((Test-InScopeToken -Token $token) -and
                -not $trackedSet.Contains($token) -and
                -not $trackedFiles.Where({ $_.StartsWith("$token/", [StringComparison]::Ordinal) }).Count) {
                Add-Finding -Findings $findings -File $markdownFile -Line ($index + 1) `
                    -Rule 'tracked-path' -Message "Untracked repository path reference '$token'."
            }
        }

        foreach ($match in [regex]::Matches(
                $line,
                '(?<!\!)\[[^\]]*\]\((?<target><[^>]+>|[^\s\)]+)(?:\s+[^)]*)?\)'
            )) {
            $target = $match.Groups['target'].Value.Trim('<', '>')
            if ($target -match '^(?:[A-Za-z][A-Za-z0-9+.-]*://|mailto:|//)') {
                continue
            }

            $targetParts = $target.Split('#', 2)
            $pathPart = $targetParts[0]
            $anchor = if ($targetParts.Count -eq 2) { $targetParts[1] } else { $null }

            $targetFile = $markdownFile
            if (-not [string]::IsNullOrEmpty($pathPart)) {
                $relativePath = $pathPart -replace '\\', '/'
                $targetFile = [IO.Path]::GetFullPath(
                    (Join-Path (Split-Path -Parent $fullPath) $relativePath)
                ).Substring($RepositoryRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                if (-not $trackedSet.Contains($targetFile)) {
                    Add-Finding -Findings $findings -File $markdownFile -Line ($index + 1) `
                        -Rule 'relative-link' -Message "Broken relative link target '$target'."
                    continue
                }
            }

            if (-not [string]::IsNullOrEmpty($anchor) -and $targetFile.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
                if (-not $headingCache.ContainsKey($targetFile)) {
                    $targetPath = Join-Path $RepositoryRoot ($targetFile -replace '/', [IO.Path]::DirectorySeparatorChar)
                    $headingCache[$targetFile] = Get-HeadingSlugs -Lines @(Get-Content -LiteralPath $targetPath)
                }
                if (-not $headingCache[$targetFile].Contains($anchor)) {
                    Add-Finding -Findings $findings -File $markdownFile -Line ($index + 1) `
                        -Rule 'markdown-anchor' -Message "Broken markdown anchor '#$anchor' in '$target'."
                }
            }
        }
    }
}

foreach ($eol in @(Invoke-Git -Arguments @('ls-files', '--eol', '--', '*.md') `
            -FailureMessage 'Unable to inspect markdown line endings')) {
    if ($eol -notmatch '\sw/(?<ending>crlf|mixed)\s') {
        continue
    }
    $ending = $Matches.ending
    if ($eol -match "`t(?<file>.+)$") {
        Add-Finding -Findings $findings -File $Matches.file -Line 1 `
            -Rule 'lf-line-endings' -Message "Markdown worktree line endings are $ending."
    }
}

foreach ($finding in $findings) {
    $rendered = "$($finding.File):$($finding.Line) [$($finding.Rule)] $($finding.Message)"
    Write-Output $rendered
    if ($Advisory) {
        Write-Output "::warning file=$($finding.File),line=$($finding.Line)::$($finding.Message)"
    }
}
Write-Output "Documentation reference findings: $($findings.Count)"

if ($Advisory -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $summaryLines = [System.Collections.Generic.List[string]]::new()
    $summaryLines.Add('## Documentation reference check')
    $summaryLines.Add('')
    $summaryLines.Add('| File | Line | Rule | Message |')
    $summaryLines.Add('| --- | ---: | --- | --- |')
    foreach ($finding in $findings) {
        $summaryLines.Add(
            "| $($finding.File) | $($finding.Line) | $($finding.Rule) | $($finding.Message) |"
        )
    }
    if ($findings.Count -eq 0) {
        $summaryLines.Add('| - | - | - | No findings. |')
    }
    $summaryLines.Add('')
    [IO.File]::AppendAllText(
        $env:GITHUB_STEP_SUMMARY,
        ([string]::Join("`n", [string[]]$summaryLines)),
        [Text.UTF8Encoding]::new($false)
    )
}

if (-not $Advisory -and $findings.Count -gt 0) {
    exit 1
}
