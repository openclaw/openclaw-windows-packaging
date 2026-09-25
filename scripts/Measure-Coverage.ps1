<#
.SYNOPSIS
    Collects or summarizes Cobertura coverage for the managed OpenClaw test suite.

.DESCRIPTION
    Without -CoberturaPath, runs the launcher test project using the explicitly
    selected tests\coverage.runsettings file. With -CoberturaPath, only reports
    the supplied Cobertura result. Results are recomputed from source lines so
    compiler-generated duplicate classes cannot inflate coverage.

.EXAMPLE
    .\scripts\Measure-Coverage.ps1 -Uncovered -Path 'src\OpenClaw.Launcher\Session\*'

.EXAMPLE
    .\scripts\Measure-Coverage.ps1 -CoberturaPath artifacts\coverage\previous\coverage.cobertura.xml
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [string]$Configuration = 'Debug',
    [string]$Path = '*',
    [switch]$Uncovered,
    [ValidateRange(1, 1000)]
    [int]$Top = 15,
    [string]$BaselinePath,
    [string]$CoberturaPath,
    [string]$OutputDirectory,
    [switch]$PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$defaultOutputDirectory = [string]::IsNullOrWhiteSpace($OutputDirectory)
if ($defaultOutputDirectory) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\coverage\$timestamp-$([guid]::NewGuid().ToString('N'))"
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$pathFilter = $Path.Replace('\', '/')

function Get-RepositoryRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$FileName
    )

    $candidate = $FileName.Replace('/', '\')
    if (-not [IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $repositoryRoot $candidate
    }

    $fullPath = [IO.Path]::GetFullPath($candidate)
    $rootPrefix = $repositoryRoot.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    return $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
}

function Get-ConditionCounts {
    param([string]$ConditionCoverage)

    if ([string]::IsNullOrWhiteSpace($ConditionCoverage)) {
        return $null
    }

    $match = [regex]::Match($ConditionCoverage, '\((?<covered>\d+)\/(?<total>\d+)\)')
    if (-not $match.Success) {
        return $null
    }

    $total = [int]$match.Groups['total'].Value
    if ($total -le 0) {
        return $null
    }

    [pscustomobject]@{
        Covered = [int]$match.Groups['covered'].Value
        Total = $total
    }
}

function Get-CoverageData {
    param(
        [Parameter(Mandatory)]
        [string]$CoveragePath
    )

    if (-not (Test-Path -LiteralPath $CoveragePath -PathType Leaf)) {
        throw "Cobertura file '$CoveragePath' does not exist."
    }

    try {
        $settings = [System.Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [System.Xml.XmlReader]::Create($CoveragePath, $settings)
        try {
            $document = [System.Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        finally {
            $reader.Dispose()
        }
    }
    catch {
        throw "Could not parse Cobertura file '$CoveragePath': $($_.Exception.Message)"
    }

    $classNodes = $document.SelectNodes('/coverage/packages/package/classes/class')
    if ($null -eq $classNodes) {
        throw "Cobertura file '$CoveragePath' has no class results."
    }

    $files = @{}
    foreach ($class in $classNodes) {
        $relativePath = Get-RepositoryRelativePath -FileName $class.GetAttribute('filename')
        if ($null -eq $relativePath) {
            continue
        }

        if (-not $files.ContainsKey($relativePath)) {
            $package = $class.ParentNode.ParentNode
            $files[$relativePath] = [ordered]@{
                Path = $relativePath
                Assembly = $package.GetAttribute('name')
                Lines = @{}
                Branches = @{}
            }
        }

        foreach ($line in $class.SelectNodes('./lines/line')) {
            $numberText = $line.GetAttribute('number')
            $hitsText = $line.GetAttribute('hits')
            $lineNumber = 0
            $hits = 0
            if (-not [int]::TryParse($numberText, [ref]$lineNumber) -or
                -not [int]::TryParse($hitsText, [ref]$hits) -or $lineNumber -le 0) {
                throw "Cobertura file '$CoveragePath' contains an invalid line in '$relativePath'."
            }

            if (-not $files[$relativePath].Lines.ContainsKey($lineNumber) -or $hits -gt $files[$relativePath].Lines[$lineNumber]) {
                $files[$relativePath].Lines[$lineNumber] = $hits
            }

            $condition = Get-ConditionCounts -ConditionCoverage $line.GetAttribute('condition-coverage')
            if ($null -ne $condition) {
                $current = $files[$relativePath].Branches[$lineNumber]
                if ($null -eq $current -or
                    ($condition.Covered / $condition.Total) -gt ($current.Covered / $current.Total) -or
                    (($condition.Covered / $condition.Total) -eq ($current.Covered / $current.Total) -and
                     $condition.Covered -gt $current.Covered)) {
                    $files[$relativePath].Branches[$lineNumber] = $condition
                }
            }
        }
    }

    $result = foreach ($file in $files.Values) {
        $lineNumbers = @($file.Lines.Keys | Sort-Object)
        $coveredLines = @($lineNumbers | Where-Object { $file.Lines[$_] -gt 0 })
        $branches = @($file.Branches.Values)
        $coveredBranches = @($branches | ForEach-Object { $_.Covered } | Measure-Object -Sum).Sum
        $totalBranches = @($branches | ForEach-Object { $_.Total } | Measure-Object -Sum).Sum
        [pscustomobject]@{
            Path = $file.Path
            Assembly = $file.Assembly
            TotalLines = $lineNumbers.Count
            CoveredLines = $coveredLines.Count
            LineRate = if ($lineNumbers.Count) { $coveredLines.Count / $lineNumbers.Count } else { 0 }
            TotalBranches = [int]($totalBranches ?? 0)
            CoveredBranches = [int]($coveredBranches ?? 0)
            BranchRate = if ($totalBranches) { $coveredBranches / $totalBranches } else { 0 }
            CoveredLineNumbers = $coveredLines
            UncoveredLines = @($lineNumbers | Where-Object { $file.Lines[$_] -le 0 })
            PartialBranchLines = @($file.Branches.Keys | Where-Object {
                $file.Branches[$_].Covered -lt $file.Branches[$_].Total
            } | Sort-Object)
        }
    }

    return @($result | Sort-Object Path)
}

function Get-CoverageSummary {
    param([object[]]$Files)

    $totalLines = @($Files | ForEach-Object TotalLines | Measure-Object -Sum).Sum
    $coveredLines = @($Files | ForEach-Object CoveredLines | Measure-Object -Sum).Sum
    $totalBranches = @($Files | ForEach-Object TotalBranches | Measure-Object -Sum).Sum
    $coveredBranches = @($Files | ForEach-Object CoveredBranches | Measure-Object -Sum).Sum
    [pscustomobject]@{
        TotalLines = [int]($totalLines ?? 0)
        CoveredLines = [int]($coveredLines ?? 0)
        LineRate = if ($totalLines) { $coveredLines / $totalLines } else { 0 }
        TotalBranches = [int]($totalBranches ?? 0)
        CoveredBranches = [int]($coveredBranches ?? 0)
        BranchRate = if ($totalBranches) { $coveredBranches / $totalBranches } else { 0 }
    }
}

function Get-TestRunCount {
    param(
        [Parameter(Mandatory)]
        [string]$TrxPath
    )

    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw "Test results file '$TrxPath' does not exist."
    }

    try {
        $settings = [System.Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [System.Xml.XmlReader]::Create($TrxPath, $settings)
        try {
            $document = [System.Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        finally {
            $reader.Dispose()
        }
    }
    catch {
        throw "Could not parse test results file '$TrxPath': $($_.Exception.Message)"
    }

    $counters = $document.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
    $total = 0
    if ($null -eq $counters -or
        -not [int]::TryParse($counters.GetAttribute('total'), [ref]$total) -or
        $total -lt 0) {
        throw "Test results file '$TrxPath' has no valid total test count."
    }

    return $total
}

function Format-LineRanges {
    param([int[]]$Lines)

    $ordered = @($Lines | Sort-Object -Unique)
    if ($ordered.Count -eq 0) {
        return ''
    }

    $ranges = [Collections.Generic.List[string]]::new()
    $start = $ordered[0]
    $previous = $start
    if ($ordered.Count -gt 1) {
        foreach ($line in $ordered[1..($ordered.Count - 1)]) {
            if ($line -eq $previous + 1) {
                $previous = $line
                continue
            }
            $ranges.Add($(if ($start -eq $previous) { "$start" } else { "$start-$previous" }))
            $start = $line
            $previous = $line
        }
    }
    $ranges.Add($(if ($start -eq $previous) { "$start" } else { "$start-$previous" }))
    return $ranges -join ', '
}

function Get-UniqueCoveragePath {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [IO.FileInfo[]]$CoverageFiles,

        [Parameter(Mandatory)]
        [string]$DirectoryPath
    )

    $uniqueCoverageFiles = @{}
    foreach ($coverageFile in $CoverageFiles) {
        $hash = (Get-FileHash -LiteralPath $coverageFile.FullName -Algorithm SHA256).Hash
        if (-not $uniqueCoverageFiles.ContainsKey($hash)) {
            $uniqueCoverageFiles[$hash] = $coverageFile.FullName
        }
    }
    if ($uniqueCoverageFiles.Count -ne 1) {
        throw "Expected one unique Cobertura file under '$DirectoryPath'; found $($uniqueCoverageFiles.Count) distinct reports across $($CoverageFiles.Count) files."
    }

    return @($uniqueCoverageFiles.Values)[0]
}

function Get-BaselineCoveragePath {
    param([string]$InputPath)

    if (-not (Test-Path -LiteralPath $InputPath)) {
        throw "Baseline path '$InputPath' does not exist."
    }
    if (Test-Path -LiteralPath $InputPath -PathType Leaf) {
        return [IO.Path]::GetFullPath($InputPath)
    }

    $matches = @(Get-ChildItem -LiteralPath $InputPath -Recurse -File -Filter '*.cobertura.xml')
    return Get-UniqueCoveragePath -CoverageFiles $matches -DirectoryPath $InputPath
}

New-Item -ItemType Directory -Path $OutputDirectory -Force:(!$defaultOutputDirectory) | Out-Null
if ([string]::IsNullOrWhiteSpace($CoberturaPath)) {
    $testProject = Join-Path $repositoryRoot 'tests\OpenClaw.Launcher.Tests\OpenClaw.Launcher.Tests.csproj'
    $runSettings = Join-Path $repositoryRoot 'tests\coverage.runsettings'
    if (-not (Test-Path -LiteralPath $runSettings -PathType Leaf)) {
        throw "Coverage runsettings file '$runSettings' does not exist."
    }

    $arguments = @('test', $testProject, '--configuration', $Configuration, '--collect', 'Code Coverage;Format=Cobertura',
        '--settings', $runSettings, '--results-directory', $OutputDirectory, '--blame-hang-timeout', '3min',
        '--logger', 'trx;LogFileName=coverage.trx')
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $arguments += '--filter', $Filter
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed with exit code $LASTEXITCODE. Results directory: '$OutputDirectory'. No coverage summary was produced."
    }

    $trxFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Filter 'coverage.trx')
    if ($trxFiles.Count -ne 1) {
        throw "Expected exactly one TRX file under '$OutputDirectory'; found $($trxFiles.Count)."
    }
    if ((Get-TestRunCount -TrxPath $trxFiles[0].FullName) -eq 0) {
        throw "No tests matched filter '$Filter'. Results directory: '$OutputDirectory'. No coverage summary was produced."
    }

    $coverageFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Filter '*.cobertura.xml')
    $CoberturaPath = Get-UniqueCoveragePath -CoverageFiles $coverageFiles -DirectoryPath $OutputDirectory
}
elseif (-not [IO.Path]::IsPathRooted($CoberturaPath)) {
    $CoberturaPath = Join-Path $repositoryRoot $CoberturaPath
}

$files = @(Get-CoverageData -CoveragePath ([IO.Path]::GetFullPath($CoberturaPath)) |
    Where-Object { $_.Path -like $pathFilter })
if ($files.Count -eq 0 -or (@($files | ForEach-Object TotalLines | Measure-Object -Sum).Sum -eq 0)) {
    throw "No coverage recorded; check -Filter '$Path'."
}

$overall = Get-CoverageSummary -Files $files
$assemblies = @($files | Group-Object Assembly | ForEach-Object {
    $summary = Get-CoverageSummary -Files $_.Group
    [pscustomobject]@{
        Assembly = $_.Name
        Files = $_.Count
        TotalLines = $summary.TotalLines
        CoveredLines = $summary.CoveredLines
        LineRate = $summary.LineRate
        TotalBranches = $summary.TotalBranches
        CoveredBranches = $summary.CoveredBranches
        BranchRate = $summary.BranchRate
    }
} | Sort-Object Assembly)

$delta = @()
$baselineOverall = $null
if ($BaselinePath) {
    $baselineFiles = @(Get-CoverageData -CoveragePath (Get-BaselineCoveragePath -InputPath $BaselinePath) |
        Where-Object { $_.Path -like $pathFilter })
    $baselineOverall = Get-CoverageSummary -Files $baselineFiles
    $baselineByPath = @{}
    $baselineFiles | ForEach-Object { $baselineByPath[$_.Path] = $_ }
    $currentByPath = @{}
    $files | ForEach-Object { $currentByPath[$_.Path] = $_ }
    foreach ($filePath in @($baselineByPath.Keys + $currentByPath.Keys | Sort-Object -Unique)) {
        $before = if ($baselineByPath.ContainsKey($filePath)) { $baselineByPath[$filePath] } else { $null }
        $after = if ($currentByPath.ContainsKey($filePath)) { $currentByPath[$filePath] } else { $null }
        $beforeLines = if ($before) { @($before.CoveredLineNumbers) } else { @() }
        $afterLines = if ($after) { @($after.CoveredLineNumbers) } else { @() }
        $delta += [pscustomobject]@{
            Path = $filePath
            NewlyCoveredLines = @($afterLines | Where-Object { $_ -notin $beforeLines })
            NewlyUncoveredLines = @($beforeLines | Where-Object { $_ -notin $afterLines })
        }
    }
}

$overallDelta = $null
if ($baselineOverall) {
    $overallDelta = [pscustomobject]@{
        CoveredLines = $overall.CoveredLines - $baselineOverall.CoveredLines
        TotalLines = $overall.TotalLines - $baselineOverall.TotalLines
        LineRate = $overall.LineRate - $baselineOverall.LineRate
        CoveredBranches = $overall.CoveredBranches - $baselineOverall.CoveredBranches
        TotalBranches = $overall.TotalBranches - $baselineOverall.TotalBranches
        BranchRate = $overall.BranchRate - $baselineOverall.BranchRate
    }
}

$reportFiles = @($files | Sort-Object LineRate, Path | Select-Object -First $Top)
$summaryPath = Join-Path $OutputDirectory 'summary.md'
$markdown = [Collections.Generic.List[string]]::new()
$markdown.Add('# Coverage summary')
$markdown.Add('')
$markdown.Add("| Lines | Branches |")
$markdown.Add("| --- | --- |")
$markdown.Add(("| {0:N2}% ({1}/{2}) | {3:N2}% ({4}/{5}) |" -f ($overall.LineRate * 100), $overall.CoveredLines, $overall.TotalLines, ($overall.BranchRate * 100), $overall.CoveredBranches, $overall.TotalBranches))
$markdown.Add('')
$markdown.Add('## Assemblies')
$markdown.Add('')
$markdown.Add('| Assembly | Lines | Branches | Files |')
$markdown.Add('| --- | --- | --- | --- |')
foreach ($assembly in $assemblies) {
    $markdown.Add(("| {0} | {1:N2}% ({2}/{3}) | {4:N2}% ({5}/{6}) | {7} |" -f $assembly.Assembly, ($assembly.LineRate * 100), $assembly.CoveredLines, $assembly.TotalLines, ($assembly.BranchRate * 100), $assembly.CoveredBranches, $assembly.TotalBranches, $assembly.Files))
}
$markdown.Add('')
$markdown.Add("## Lowest $($reportFiles.Count) files")
$markdown.Add('')
$markdown.Add('| File | Lines | Branches |')
$markdown.Add('| --- | --- | --- |')
foreach ($file in $reportFiles) {
    $markdown.Add(("| {0} | {1:N2}% ({2}/{3}) | {4:N2}% ({5}/{6}) |" -f $file.Path, ($file.LineRate * 100), $file.CoveredLines, $file.TotalLines, ($file.BranchRate * 100), $file.CoveredBranches, $file.TotalBranches))
}
if ($baselineOverall) {
    $markdown.Add('')
    $markdown.Add('## Since baseline')
    $markdown.Add('')
    $markdown.Add(("Lines: {0:+#;-#;0} covered, {1:+#;-#;0} total, {2:+0.00;-0.00;0.00} percentage points; branches: {3:+#;-#;0} covered, {4:+#;-#;0} total, {5:+0.00;-0.00;0.00} percentage points." -f $overallDelta.CoveredLines, $overallDelta.TotalLines, ($overallDelta.LineRate * 100), $overallDelta.CoveredBranches, $overallDelta.TotalBranches, ($overallDelta.BranchRate * 100)))
    $markdown.Add('')
    $markdown.Add('| File | Newly covered lines | Newly uncovered lines |')
    $markdown.Add('| --- | --- | --- |')
    $changedFiles = @($delta | Where-Object {
        $_.NewlyCoveredLines.Count -gt 0 -or $_.NewlyUncoveredLines.Count -gt 0
    } | Sort-Object Path)
    if ($changedFiles.Count -eq 0) {
        $markdown.Add('| No line coverage changes | | |')
    }
    foreach ($change in $changedFiles) {
        $markdown.Add(("| {0} | {1} | {2} |" -f $change.Path, (Format-LineRanges $change.NewlyCoveredLines), (Format-LineRanges $change.NewlyUncoveredLines)))
    }
}
if ($Uncovered) {
    $markdown.Add('')
    $markdown.Add('## Uncovered lines')
    $markdown.Add('')
    foreach ($file in $files) {
        $markdown.Add(("* **{0}**: {1}; partial branches: {2}" -f $file.Path, (Format-LineRanges $file.UncoveredLines), (Format-LineRanges $file.PartialBranchLines)))
    }
}
[IO.File]::WriteAllLines($summaryPath, $markdown, [Text.UTF8Encoding]::new($false))

Write-Host ("Lines: {0:N2}% ({1}/{2}); branches: {3:N2}% ({4}/{5})" -f ($overall.LineRate * 100), $overall.CoveredLines, $overall.TotalLines, ($overall.BranchRate * 100), $overall.CoveredBranches, $overall.TotalBranches)
if ($overallDelta) {
    Write-Host ("Since baseline: lines {0:+#;-#;0} covered, {1:+#;-#;0} total, {2:+0.00;-0.00;0.00} pp; branches {3:+#;-#;0} covered, {4:+#;-#;0} total, {5:+0.00;-0.00;0.00} pp" -f $overallDelta.CoveredLines, $overallDelta.TotalLines, ($overallDelta.LineRate * 100), $overallDelta.CoveredBranches, $overallDelta.TotalBranches, ($overallDelta.BranchRate * 100))
    foreach ($change in $changedFiles) {
        Write-Host ("{0}: newly covered {1}; newly uncovered {2}" -f $change.Path, (Format-LineRanges $change.NewlyCoveredLines), (Format-LineRanges $change.NewlyUncoveredLines))
    }
    if ($changedFiles.Count -eq 0) {
        Write-Host 'No line coverage changes in selected files.'
    }
}
$assemblies | Format-Table Assembly, Files, @{ Label = 'Lines'; Expression = { '{0:N2}%' -f ($_.LineRate * 100) } }, @{ Label = 'Branches'; Expression = { '{0:N2}%' -f ($_.BranchRate * 100) } } | Out-Host
$reportFiles | Format-Table Path, @{ Label = 'Lines'; Expression = { '{0:N2}%' -f ($_.LineRate * 100) } }, @{ Label = 'Branches'; Expression = { '{0:N2}%' -f ($_.BranchRate * 100) } } | Out-Host
if ($Uncovered) {
    $files | ForEach-Object {
        Write-Host "$($_.Path): $(Format-LineRanges $_.UncoveredLines); partial branches: $(Format-LineRanges $_.PartialBranchLines)"
    }
}
Write-Host "Cobertura: $CoberturaPath"
Write-Host "Summary: $summaryPath"

if ($PassThru) {
    [pscustomobject]@{
        CoberturaPath = $CoberturaPath
        SummaryPath = $summaryPath
        Overall = $overall
        Assemblies = $assemblies
        Files = $files
        Delta = $delta
        BaselineOverall = $baselineOverall
        OverallDelta = $overallDelta
    }
}
