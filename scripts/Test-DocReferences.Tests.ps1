[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Test-DocReferences.ps1'
$temporaryBase = if ([string]::IsNullOrWhiteSpace($env:TEMP)) {
    [IO.Path]::GetTempPath()
}
else {
    $env:TEMP
}
$testRoot = Join-Path $temporaryBase "openclaw-doc-references-$([guid]::NewGuid().ToString('N'))"

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Reason
    )

    if (-not $Condition) {
        throw $Reason
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Expected,

        [Parameter(Mandatory)]
        [string]$Reason
    )

    if (-not $Text.Contains($Expected, [StringComparison]::Ordinal)) {
        throw "$Reason Expected '$Expected' in '$Text'."
    }
}

function Write-FixtureFile {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Content,

        [switch]$CrLf,

        [switch]$Track
    )

    $fullPath = Join-Path $Root ($Path -replace '/', [IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path (Split-Path -Parent $fullPath) -Force | Out-Null
    $newline = if ($CrLf) { "`r`n" } else { "`n" }
    [IO.File]::WriteAllText(
        $fullPath,
        ($Content -replace "`r?`n", $newline),
        [Text.UTF8Encoding]::new($false)
    )
    if ($Track) {
        & git -C $Root add -- $Path
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to track fixture file '$Path'."
        }
    }
}

function New-Fixture {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $root = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    & git -C $root init --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to initialize fixture '$Name'."
    }
    return $root
}

function Invoke-Checker {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [switch]$Advisory
    )

    $output = @(& $scriptPath -RepositoryRoot $Root -Advisory:$Advisory)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = [string]::Join("`n", [string[]]$output)
    }
}

New-Item -Path $testRoot -ItemType Directory | Out-Null
try {
    $clean = New-Fixture 'clean'
    Write-FixtureFile $clean 'README.md' '# Clean' -Track
    $result = Invoke-Checker $clean
    Assert-True ($result.ExitCode -eq 0) 'A clean fixture must pass.'
    Assert-Contains $result.Output 'Documentation reference findings: 0' 'Clean fixture count was wrong.'

    $stale = New-Fixture 'stale'
    $staleContent = @'
# Stale
`src/Nope/Nope.csproj`
'@
    Write-FixtureFile $stale 'README.md' $staleContent -Track
    $result = Invoke-Checker $stale
    Assert-True ($result.ExitCode -eq 1) 'A stale repository path must fail.'
    Assert-Contains $result.Output 'README.md:2 [tracked-path]' 'Stale path line number was wrong.'
    Assert-Contains $result.Output "Untracked repository path reference 'src/Nope/Nope.csproj'." 'Stale path was not named.'

    $fencedCodeBlock = New-Fixture 'fenced-code-block'
    $fencedCodeBlockContent = @'
```powershell
dotnet publish .\src\Nope\Nope.csproj
```
'@
    Write-FixtureFile $fencedCodeBlock 'README.md' $fencedCodeBlockContent -Track
    $result = Invoke-Checker $fencedCodeBlock
    Assert-True ($result.ExitCode -eq 1) 'A stale path in a fenced code block must fail.'
    Assert-Contains $result.Output 'README.md:2 [tracked-path]' 'Fenced code block line number was wrong.'
    Assert-Contains $result.Output "Untracked repository path reference 'src/Nope/Nope.csproj'." 'Fenced code block path was not named.'

    $trap = New-Fixture 'untracked-build-output'
    Write-FixtureFile $trap 'src/OpenClaw.Launcher/OpenClaw.Launcher.csproj' '<Project />' -Track
    Write-FixtureFile $trap 'src/OpenClaw.Gateway.Launcher/bin/Release/openclaw.dll' 'stale output'
    Write-FixtureFile $trap 'README.md' '`src\OpenClaw.Gateway.Launcher\OpenClaw.Gateway.Launcher.csproj`' -Track
    $result = Invoke-Checker $trap
    Assert-True ($result.ExitCode -eq 1) 'Untracked build output must not satisfy a documentation path.'
    Assert-Contains $result.Output "Untracked repository path reference 'src/OpenClaw.Gateway.Launcher/OpenClaw.Gateway.Launcher.csproj'." 'The git-index trap was not caught.'

    $outOfScope = New-Fixture 'out-of-scope'
    Write-FixtureFile $outOfScope 'README.md' @'
`%LOCALAPPDATA%\OpenClawGatewayMSIX` `app\openclaw.mjs` `artifacts\local-package` `obj\packaging` `https://example.com/x.md` `C:\Windows` `content/mxc/`
'@ -Track
    $result = Invoke-Checker $outOfScope
    Assert-True ($result.ExitCode -eq 0) 'Out-of-scope paths must not be reported.'

    $bareNames = New-Fixture 'bare-names'
    Write-FixtureFile $bareNames 'README.md' @'
`Build-MSIX.ps1` `System.Text.Json` `payload-metadata.json`
'@ -Track
    $result = Invoke-Checker $bareNames
    Assert-True ($result.ExitCode -eq 0) 'Bare filenames and namespaces must not be reported.'

    $ignoredPrefix = New-Fixture 'ignored-prefix'
    Write-FixtureFile $ignoredPrefix 'README.md' @'
`.github/actions/setup-node-env` `.github/workflows/nope.yml`
'@ -Track
    $result = Invoke-Checker $ignoredPrefix
    Assert-True ($result.ExitCode -eq 1) 'Untracked workflows path must fail.'
    Assert-Contains $result.Output "Untracked repository path reference '.github/workflows/nope.yml'." 'Workflow path was not named.'
    Assert-True (-not $result.Output.Contains('.github/actions/setup-node-env', [StringComparison]::Ordinal)) 'The upstream action exception was reported.'

    $directory = New-Fixture 'directory'
    Write-FixtureFile $directory 'src/OpenClaw.Launcher/OpenClaw.Launcher.csproj' '<Project />' -Track
    Write-FixtureFile $directory 'README.md' '`src/OpenClaw.Launcher`' -Track
    $result = Invoke-Checker $directory
    Assert-True ($result.ExitCode -eq 0) 'A tracked directory prefix must pass.'

    $links = New-Fixture 'links'
    Write-FixtureFile $links 'docs/target.md' '# Target' -Track
    $linksContent = @'
[Valid](docs/target.md)
[Broken](docs/missing.md)
'@
    Write-FixtureFile $links 'README.md' $linksContent -Track
    $result = Invoke-Checker $links
    Assert-True ($result.ExitCode -eq 1) 'A broken relative link must fail.'
    Assert-Contains $result.Output 'README.md:2 [relative-link]' 'Broken relative link line was wrong.'
    Assert-True (-not $result.Output.Contains('docs/target.md', [StringComparison]::Ordinal)) 'Valid relative link was reported.'

    $nestedLink = New-Fixture 'nested-link'
    Write-FixtureFile $nestedLink 'docs/docs/target.md' '# Target' -Track
    Write-FixtureFile $nestedLink 'docs/index.md' @'
# Index
[Valid](docs/target.md)
[Broken](docs/missing.md)
'@ -Track
    $result = Invoke-Checker $nestedLink
    Assert-True ($result.ExitCode -eq 1) 'A broken nested relative link must fail.'
    Assert-Contains $result.Output 'docs/index.md:3 [relative-link]' 'Broken nested relative link line was wrong.'
    Assert-True (-not $result.Output.Contains("Untracked repository path reference 'docs/target.md'.", [StringComparison]::Ordinal)) 'A valid document-relative link was checked from the repository root.'

    $anchors = New-Fixture 'anchors'
    Write-FixtureFile $anchors 'README.md' @'
## `clawctl` output style
[Valid](#clawctl-output-style)
[Broken](#not-present)
'@ -Track
    $result = Invoke-Checker $anchors
    Assert-True ($result.ExitCode -eq 1) 'An invalid anchor must fail.'
    Assert-Contains $result.Output 'README.md:3 [markdown-anchor]' 'Broken anchor line was wrong.'
    Assert-True (-not $result.Output.Contains('#clawctl-output-style', [StringComparison]::Ordinal)) 'Valid anchor was reported.'

    $headinglessAnchor = New-Fixture 'headingless-anchor'
    Write-FixtureFile $headinglessAnchor 'docs/headingless.md' 'No headings here.' -Track
    Write-FixtureFile $headinglessAnchor 'README.md' '[Broken](docs/headingless.md#missing)' -Track
    $result = Invoke-Checker $headinglessAnchor
    Assert-True ($result.ExitCode -eq 1) 'An anchor into a headingless document must fail without crashing.'
    Assert-Contains $result.Output 'README.md:1 [markdown-anchor]' 'Headingless anchor finding was missing.'
    $result = Invoke-Checker $headinglessAnchor -Advisory
    Assert-True ($result.ExitCode -eq 0) 'A headingless anchor must not crash advisory mode.'
    Assert-Contains $result.Output '::warning file=README.md,line=1::' 'Headingless advisory annotation was missing.'

    $singleHeadingAnchor = New-Fixture 'single-heading-anchor'
    Write-FixtureFile $singleHeadingAnchor 'docs/target.md' '# Target details' -Track
    Write-FixtureFile $singleHeadingAnchor 'README.md' '[Broken](docs/target.md#target)' -Track
    $result = Invoke-Checker $singleHeadingAnchor
    Assert-True ($result.ExitCode -eq 1) 'A substring of a sole heading slug must not pass.'
    Assert-Contains $result.Output 'README.md:1 [markdown-anchor]' 'Single-heading substring anchor finding was missing.'

    $lineEndings = New-Fixture 'line-endings'
    Write-FixtureFile $lineEndings 'lf.md' '# LF' -Track
    Write-FixtureFile $lineEndings 'crlf.md' "# CRLF`n" -CrLf -Track
    $result = Invoke-Checker $lineEndings
    Assert-True ($result.ExitCode -eq 1) 'CRLF markdown must fail.'
    Assert-Contains $result.Output 'crlf.md:1 [lf-line-endings]' 'CRLF finding was missing.'
    Assert-True ($result.Output -notmatch '(?m)^lf\.md:1 \[lf-line-endings\]') 'LF markdown was reported.'

    $advisory = New-Fixture 'advisory'
    Write-FixtureFile $advisory 'README.md' '`src/Nope/Nope.csproj`' -Track
    $result = Invoke-Checker $advisory -Advisory
    Assert-True ($result.ExitCode -eq 0) 'Advisory mode must exit zero.'
    Assert-Contains $result.Output '::warning file=README.md,line=1::' 'Advisory annotation was missing.'

    $notGit = Join-Path $testRoot 'not-git'
    New-Item -ItemType Directory -Path $notGit | Out-Null
    $threw = $false
    try {
        & $scriptPath -RepositoryRoot $notGit | Out-Null
    }
    catch {
        $threw = $true
    }
    Assert-True $threw 'A non-git directory must throw.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Documentation reference tests passed.'
