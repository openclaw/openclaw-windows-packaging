[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Measure-Coverage.ps1'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testDirectoryName = ".coverage-script-test-$([guid]::NewGuid().ToString('N'))"
$testRoot = Join-Path $repositoryRoot $testDirectoryName

function Assert-Equal {
    param(
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)][string]$Reason
    )

    if ($Actual -ne $Expected) {
        throw "$Reason. Expected '$Expected', got '$Actual'."
    }
}

function Assert-SequenceEqual {
    param(
        [Parameter(Mandatory)][object[]]$Actual,
        [Parameter(Mandatory)][object[]]$Expected,
        [Parameter(Mandatory)][string]$Reason
    )

    if (($Actual -join '|') -ne ($Expected -join '|')) {
        throw "$Reason. Expected '$($Expected -join ', ')', got '$($Actual -join ', ')'."
    }
}

function Assert-Fails {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw
        }
        return
    }

    throw "Expected failure matching '$MessagePattern'."
}

function Write-Cobertura {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $absoluteSource = Join-Path $testRoot 'absolute.cs'
    $relativeSource = Join-Path $testRoot 'relative.cs'
    $baselineOnlySource = Join-Path $testRoot 'baseline-only.cs'
    [IO.File]::WriteAllText($absoluteSource, 'class Absolute {}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($relativeSource, 'class Relative {}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($baselineOnlySource, 'class BaselineOnly {}', [Text.UTF8Encoding]::new($false))

    $currentCoverage = Join-Path $testRoot 'current.cobertura.xml'
    Write-Cobertura -Path $currentCoverage -Content @"
<coverage>
  <packages>
    <package name="Assembly.One"><classes>
      <class name="Absolute" filename="$([Security.SecurityElement]::Escape($absoluteSource))"><lines>
        <line number="10" hits="0" branch="True" condition-coverage="50% (1/2)" />
        <line number="11" hits="0" />
        <line number="12" hits="2" />
        <line number="13" hits="0" />
        <line number="14" hits="0" />
      </lines></class>
      <class name="Absolute.Generated" filename="$([Security.SecurityElement]::Escape($absoluteSource))"><lines>
        <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" />
      </lines></class>
      <class name="Relative.One" filename="$testDirectoryName\relative.cs"><lines>
        <line number="20" hits="1" />
      </lines></class>
      <class name="Relative.Two" filename="$testDirectoryName/relative.cs"><lines>
        <line number="21" hits="0" />
      </lines></class>
    </classes></package>
  </packages>
</coverage>
"@

    $baselineCoverage = Join-Path $testRoot 'baseline.cobertura.xml'
    Write-Cobertura -Path $baselineCoverage -Content @"
<coverage><packages><package name="Assembly.One"><classes>
  <class name="Absolute" filename="$([Security.SecurityElement]::Escape($absoluteSource))"><lines>
    <line number="10" hits="0" /><line number="11" hits="1" />
  </lines></class>
  <class name="BaselineOnly" filename="$testDirectoryName\baseline-only.cs"><lines>
    <line number="8" hits="1" />
  </lines></class>
</classes></package></packages></coverage>
"@

    $duplicateBaselineDirectory = Join-Path $testRoot 'baseline-duplicates'
    New-Item -ItemType Directory -Path $duplicateBaselineDirectory | Out-Null
    Copy-Item -LiteralPath $baselineCoverage -Destination (Join-Path $duplicateBaselineDirectory 'first.cobertura.xml')
    Copy-Item -LiteralPath $baselineCoverage -Destination (Join-Path $duplicateBaselineDirectory 'second.cobertura.xml')
    $mixedBaselineDirectory = Join-Path $testRoot 'baseline-mixed'
    New-Item -ItemType Directory -Path $mixedBaselineDirectory | Out-Null
    Copy-Item -LiteralPath $baselineCoverage -Destination (Join-Path $mixedBaselineDirectory 'baseline.cobertura.xml')
    Copy-Item -LiteralPath $currentCoverage -Destination (Join-Path $mixedBaselineDirectory 'current.cobertura.xml')

    $outputDirectory = Join-Path $testRoot 'output'
    $result = & $scriptPath -CoberturaPath $currentCoverage -BaselinePath $duplicateBaselineDirectory `
        -OutputDirectory $outputDirectory -Uncovered -PassThru

    Assert-Equal -Actual $result.Overall.TotalLines -Expected 7 -Reason 'Duplicate classes must not inflate line totals'
    Assert-Equal -Actual $result.Overall.CoveredLines -Expected 3 -Reason 'A duplicate line must be covered when any class reports hits'
    Assert-Equal -Actual $result.Overall.TotalBranches -Expected 2 -Reason 'Duplicate branch reports must aggregate once'
    Assert-Equal -Actual $result.Overall.CoveredBranches -Expected 1 -Reason 'Branch coverage must retain the best duplicate result'
    Assert-Equal -Actual $result.Files.Count -Expected 2 -Reason 'Multiple classes for one file must produce one file result'
    Assert-SequenceEqual -Actual @($result.Files[0].UncoveredLines) -Expected @(11, 13, 14) -Reason 'Uncovered source lines must be retained'
    Assert-SequenceEqual -Actual @($result.Files[0].PartialBranchLines) -Expected @(10) -Reason 'Partial branch lines must be reported'

    $summary = [IO.File]::ReadAllText($result.SummaryPath)
    if ($summary -notmatch '11, 13-14; partial branches: 10') {
        throw 'The uncovered summary did not compact adjacent ranges or include partial branches.'
    }
    if ($summary -notmatch '## Since baseline' -or
        $summary -notmatch '\| .*absolute\.cs \| 10, 12 \| 11 \|' -or
        $summary -notmatch '\| .*relative\.cs \| 20 \|  \|') {
        throw 'The baseline summary did not report overall changes and per-file covered/uncovered lines.'
    }

    $deltaByPath = @{}
    $result.Delta | ForEach-Object { $deltaByPath[$_.Path] = $_ }
    Assert-SequenceEqual -Actual @($deltaByPath["$testDirectoryName/absolute.cs"].NewlyCoveredLines) -Expected @(10, 12) -Reason 'Delta must include newly covered lines'
    Assert-SequenceEqual -Actual @($deltaByPath["$testDirectoryName/absolute.cs"].NewlyUncoveredLines) -Expected @(11) -Reason 'Delta must include newly uncovered lines'
    Assert-SequenceEqual -Actual @($deltaByPath["$testDirectoryName/relative.cs"].NewlyCoveredLines) -Expected @(20) -Reason 'Delta must include current-only files'
    Assert-SequenceEqual -Actual @($deltaByPath["$testDirectoryName/baseline-only.cs"].NewlyUncoveredLines) -Expected @(8) -Reason 'Delta must include baseline-only files'

    $filtered = & $scriptPath -CoberturaPath $currentCoverage -Path "$testDirectoryName\*" `
        -OutputDirectory (Join-Path $testRoot 'filtered-output') -PassThru
    Assert-Equal -Actual $filtered.Files.Count -Expected 2 -Reason 'Repository-relative path filters must match absolute and relative Cobertura filenames'
    $explicitReuse = & $scriptPath -CoberturaPath $currentCoverage `
        -OutputDirectory $outputDirectory -PassThru
    Assert-Equal -Actual $explicitReuse.SummaryPath -Expected $result.SummaryPath `
        -Reason 'An explicitly selected output directory must still be reusable'

    $coverageDirectory = Join-Path $testRoot 'coverage-directory'
    New-Item -ItemType Directory -Path $coverageDirectory | Out-Null
    Copy-Item -LiteralPath $currentCoverage -Destination (Join-Path $coverageDirectory 'first.cobertura.xml')
    Copy-Item -LiteralPath $currentCoverage -Destination (Join-Path $coverageDirectory 'second.cobertura.xml')
    $directoryResult = & $scriptPath -CoberturaPath $coverageDirectory `
        -OutputDirectory (Join-Path $testRoot 'directory-output') -PassThru
    Assert-Equal -Actual $directoryResult.Overall.TotalLines -Expected 7 -Reason 'Report-only mode must resolve a directory containing identical collector attachments'

    Assert-Fails -Action {
        & $scriptPath -CoberturaPath $currentCoverage -Path 'does-not-match/*' `
            -OutputDirectory (Join-Path $testRoot 'empty-output') -PassThru
    } -MessagePattern 'No coverage recorded'

    Assert-Fails -Action {
        & $scriptPath -CoberturaPath (Join-Path $testRoot 'missing.cobertura.xml') `
            -OutputDirectory (Join-Path $testRoot 'missing-output') -PassThru
    } -MessagePattern 'does not exist'

    $malformedCoverage = Join-Path $testRoot 'malformed.cobertura.xml'
    Write-Cobertura -Path $malformedCoverage -Content '<coverage><packages>'
    Assert-Fails -Action {
        & $scriptPath -CoberturaPath $malformedCoverage `
            -OutputDirectory (Join-Path $testRoot 'malformed-output') -PassThru
    } -MessagePattern 'Could not parse Cobertura file'
    Assert-Fails -Action {
        & $scriptPath -CoberturaPath $currentCoverage -BaselinePath $mixedBaselineDirectory `
            -OutputDirectory (Join-Path $testRoot 'mixed-baseline-output') -PassThru
    } -MessagePattern 'Expected one unique Cobertura file'

    Assert-Fails -Action {
        & $scriptPath -Filter 'FullyQualifiedName=CoverageFilterMustNotMatchAnyTest' `
            -OutputDirectory (Join-Path $testRoot 'no-matching-tests-output')
    } -MessagePattern 'No tests matched filter'

    $isolatedRoot = Join-Path $testRoot 'isolated-repository'
    $isolatedScripts = Join-Path $isolatedRoot 'scripts'
    $isolatedTests = Join-Path $isolatedRoot 'tests'
    New-Item -ItemType Directory -Path $isolatedScripts, $isolatedTests | Out-Null
    $isolatedScript = Join-Path $isolatedScripts 'Measure-Coverage.ps1'
    Copy-Item -LiteralPath $scriptPath -Destination $isolatedScript
    [IO.File]::WriteAllText((Join-Path $isolatedTests 'coverage.runsettings'), '<RunSettings />')
    $isolatedSource = Join-Path $isolatedRoot 'sample.cs'
    [IO.File]::WriteAllText($isolatedSource, 'class Sample {}')
    $isolatedCoverage = Join-Path $isolatedRoot 'sample.cobertura.xml'
    Write-Cobertura -Path $isolatedCoverage -Content @"
<coverage><packages><package name="Assembly.One"><classes>
  <class name="Sample" filename="$([Security.SecurityElement]::Escape($isolatedSource))"><lines>
    <line number="1" hits="1" />
  </lines></class>
</classes></package></packages></coverage>
"@

    $previousExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    try {
        & {
            function Get-Date {
                param([string]$Format)
                Assert-Equal -Actual $Format -Expected 'yyyyMMdd-HHmmss' -Reason 'The report directory should retain its timestamp'
                '20300101-000000'
            }

            function dotnet {
                param([Parameter(ValueFromRemainingArguments = $true)][object[]]$Arguments)

                $directoryIndex = [Array]::IndexOf($Arguments, '--results-directory')
                if ($directoryIndex -lt 0) {
                    throw 'dotnet test did not receive a results directory.'
                }
                $directory = [string]$Arguments[$directoryIndex + 1]
                [IO.File]::WriteAllText((Join-Path $directory 'coverage.trx'),
                    '<TestRun><ResultSummary><Counters total="1" /></ResultSummary></TestRun>')
                Copy-Item -LiteralPath $isolatedCoverage -Destination (Join-Path $directory 'run.cobertura.xml')
                $global:LASTEXITCODE = 0
            }

            $first = & $isolatedScript -PassThru
            $firstDirectory = Split-Path -Parent $first.SummaryPath
            $firstTrx = Join-Path $firstDirectory 'coverage.trx'
            $firstSummary = [IO.File]::ReadAllText($first.SummaryPath)

            $second = & $isolatedScript -PassThru
            $secondDirectory = Split-Path -Parent $second.SummaryPath
            if ($firstDirectory -eq $secondDirectory) {
                throw 'Default coverage runs started at the same instant reused a results directory.'
            }
            if ($firstDirectory -notlike (Join-Path $isolatedRoot 'artifacts\coverage\20300101-000000-*') -or
                $secondDirectory -notlike (Join-Path $isolatedRoot 'artifacts\coverage\20300101-000000-*')) {
                throw 'Default coverage runs must keep their timestamp and stay under the repository artifacts directory.'
            }
            if (-not (Test-Path -LiteralPath $firstTrx -PathType Leaf) -or
                -not (Test-Path -LiteralPath (Join-Path $secondDirectory 'coverage.trx') -PathType Leaf) -or
                [IO.File]::ReadAllText($first.SummaryPath) -ne $firstSummary) {
                throw 'A second default run must leave the first run TRX and summary intact.'
            }
            Assert-Equal -Actual $first.Overall.CoveredLines -Expected 1 -Reason 'The first run must report the collected coverage'
            Assert-Equal -Actual $second.Overall.CoveredLines -Expected 1 -Reason 'The second run must report its own collected coverage'
        }
    }
    finally {
        if ($null -ne $previousExitCode) {
            $global:LASTEXITCODE = $previousExitCode.Value
        }
        else {
            Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
        }
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
