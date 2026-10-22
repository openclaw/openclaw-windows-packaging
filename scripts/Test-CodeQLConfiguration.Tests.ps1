[CmdletBinding()]
param(
    [string]$CodeQlPath = 'codeql'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$configuration = Join-Path $repositoryRoot '.github\codeql\codeql-config.yml'
$codeQl = (Get-Command $CodeQlPath -CommandType Application -ErrorAction Stop |
    Select-Object -First 1).Source
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw CodeQL configuration $([guid]::NewGuid().ToString('N'))"
)
$sourceRoot = Join-Path $testRoot 'source'
$cacheRoot = Join-Path $testRoot 'cache'

function Invoke-CodeQl {
    param([string[]]$Arguments)

    & $codeQl @Arguments "--common-caches=$cacheRoot" --threads=2 --ram=4096 --verbosity=warnings
    if ($LASTEXITCODE -ne 0) {
        throw "CodeQL $($Arguments[1]) failed with exit code $LASTEXITCODE."
    }
}

function Get-ControlFlows {
    param([string]$SarifPath)

    $report = Get-Content -LiteralPath $SarifPath -Raw | ConvertFrom-Json -Depth 100
    foreach ($run in $report.runs) {
        foreach ($result in $run.results) {
            if ($result.ruleId -notin @('cs/path-injection', 'cs/command-line-injection')) {
                continue
            }
            $sink = $result.locations[0].physicalLocation
            # Both rules link source 1; zero-length paths need not emit codeFlows.
            $sources = @($result.relatedLocations | Where-Object {
                $_.PSObject.Properties['id'] -and $_.id -eq 1
            })
            if ($sources.Count -ne 1) {
                throw "The $($result.ruleId) result has no unique source location."
            }
            $source = $sources[0].physicalLocation
            [pscustomobject]@{
                Rule = $result.ruleId
                Sink = $sink.artifactLocation.uri
                Source = $source.artifactLocation.uri
                Key = "$($result.ruleId)|$($sink.artifactLocation.uri)|$($sink.region.startLine)|$($sink.region.startColumn)|$($source.artifactLocation.uri)|$($source.region.startLine)|$($source.region.startColumn)"
            }
        }
    }
}

$cases = [ordered]@{
    EnvironmentPath = '_ = File.ReadAllText(Environment.GetEnvironmentVariable("ATTACKER_PATH")!);'
    CanonicalPath = '_ = File.ReadAllText(Path.GetFullPath(Environment.GetEnvironmentVariable("ATTACKER_CANONICAL_PATH")!));'
    ArbitraryExecutable = @'
        ProcessStartInfo start = new()
        {
            FileName = Environment.GetEnvironmentVariable("ATTACKER_EXECUTABLE")!,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--constant-argument");
        using Process? process = Process.Start(start);
'@
    ArbitraryWorkingDirectory = @'
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = @"C:\Windows\System32\cmd.exe",
            UseShellExecute = false,
            WorkingDirectory = Environment.GetEnvironmentVariable("ATTACKER_WORKING_DIRECTORY")!
        });
'@
    CommandArguments = @'
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = @"C:\Windows\System32\cmd.exe",
            UseShellExecute = false,
            Arguments = Environment.GetEnvironmentVariable("ATTACKER_ARGUMENTS")!
        });
'@
    SharedProductionEntry = 'SharedProcess.RunProduction(Environment.GetEnvironmentVariable("ATTACKER_PRODUCTION_EXECUTABLE")!);'
    KnownProfilePath = @'
        Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawGatewayMSIX", "state-transfer"));
'@
    KnownProfileWorkingDirectory = @'
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = @"C:\Windows\System32\cmd.exe",
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        });
'@
    ConstantPath = '_ = File.ReadAllText(@"C:\synthetic\constant.txt");'
}

$sharedProcess = @'
using System.Diagnostics;

namespace CodeQLControls;

internal static class SharedProcess
{
    public static void RunFixture(string executable)
    {
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        });
    }

    public static void RunProduction(string executable)
    {
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        });
    }
}
'@

$fixtureEntry = @'
using System;
using System.IO;

namespace CodeQLControls;

internal static class FixtureEntry
{
    public static void LaunchFixture()
    {
        string path = Environment.GetEnvironmentVariable("PATH")!;
        string executable = Path.Combine(path.Split(Path.PathSeparator)[0], "fixture.exe");
        SharedProcess.RunFixture(executable);
    }

    public static void ReadFixture()
    {
        _ = File.ReadAllText(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
    }
}
'@

$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@

$expectedControls = @{
    EnvironmentPath = 'cs/path-injection'
    CanonicalPath = 'cs/path-injection'
    ArbitraryExecutable = 'cs/command-line-injection'
    ArbitraryWorkingDirectory = 'cs/command-line-injection'
    CommandArguments = 'cs/command-line-injection'
    SharedProductionEntry = 'cs/command-line-injection'
    KnownProfilePath = 'cs/path-injection'
    KnownProfileWorkingDirectory = 'cs/command-line-injection'
}

New-Item -Path $testRoot -ItemType Directory | Out-Null
$failed = $true
$previousEnvironment = @{}
try {
    foreach ($name in @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH')) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, (Join-Path $testRoot $name), 'Process')
    }
    New-Item -Path (Join-Path $sourceRoot 'src'), (Join-Path $sourceRoot 'tests') -ItemType Directory -Force | Out-Null
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'Controls.csproj'), $project, $encoding)
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination $sourceRoot
    foreach ($case in $cases.GetEnumerator()) {
        $source = @"
using System;
using System.Diagnostics;
using System.IO;

namespace CodeQLControls;

internal static class $($case.Key)
{
    public static void Exercise()
    {
        $($case.Value)
    }
}
"@
        [IO.File]::WriteAllText((Join-Path $sourceRoot "src\$($case.Key).cs"), $source, $encoding)
    }
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'src\SharedProcess.cs'), $sharedProcess, $encoding)
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'tests\FixtureEntry.cs'), $fixtureEntry, $encoding)

    foreach ($variant in @('baseline', 'configured')) {
        $database = Join-Path $testRoot "$variant-db"
        $arguments = @(
            'database', 'create', $database,
            '--language=csharp', '--build-mode=none', "--source-root=$sourceRoot"
        )
        if ($variant -eq 'configured') {
            $arguments += "--codescanning-config=$configuration"
        }
        Invoke-CodeQl $arguments
        Invoke-CodeQl @(
            'database', 'analyze', $database,
            '--threat-model=local', '--format=sarif-latest',
            "--output=$(Join-Path $testRoot "$variant.sarif")"
        )
    }

    $baseline = @(Get-ControlFlows (Join-Path $testRoot 'baseline.sarif'))
    $configured = @(Get-ControlFlows (Join-Path $testRoot 'configured.sarif'))
    foreach ($control in $expectedControls.GetEnumerator()) {
        $matches = @($baseline | Where-Object {
            $_.Rule -eq $control.Value -and $_.Source -eq "src/$($control.Key).cs"
        })
        if ($matches.Count -eq 0) {
            throw "Baseline analysis did not report the $($control.Key) control."
        }
    }
    foreach ($rule in @('cs/path-injection', 'cs/command-line-injection')) {
        $fixtures = @($baseline | Where-Object {
            $_.Rule -eq $rule -and ($_.Sink -like 'tests/*' -or $_.Source -like 'tests/*')
        })
        if ($fixtures.Count -eq 0) {
            throw "Baseline analysis did not report the $rule fixture control."
        }
    }
    if (@($baseline + $configured | Where-Object { $_.Sink -eq 'src/ConstantPath.cs' }).Count -ne 0) {
        throw 'CodeQL reported the constant-path safe control.'
    }
    if (@($configured | Where-Object { $_.Sink -like 'tests/*' -or $_.Source -like 'tests/*' }).Count -ne 0) {
        throw 'The repository configuration retained test-only data flow.'
    }
    $production = @($baseline | Where-Object { $_.Sink -notlike 'tests/*' -and $_.Source -notlike 'tests/*' })
    $expectedKeys = @($production.Key | Sort-Object -Unique)
    $actualKeys = @($configured.Key | Sort-Object -Unique)
    $difference = @(Compare-Object -ReferenceObject $expectedKeys -DifferenceObject $actualKeys)
    if ($difference.Count -ne 0) {
        throw "The repository configuration changed production data flow: $($difference | Out-String)"
    }

    $failed = $false
    Write-Host "CodeQL configuration tests passed: fixture flows removed, all $($expectedKeys.Count) production controls preserved."
}
finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    try {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
    catch {
        if (-not $failed) {
            throw
        }
        Write-Warning "Could not remove '$testRoot': $($_.Exception.Message)"
    }
}
