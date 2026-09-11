<#
.SYNOPSIS
    Verifies the launcher's startup path inside a published NativeAOT binary.

.DESCRIPTION
    clawctl parses its arguments with System.CommandLine. Two of its behaviors
    cannot be proven by the xUnit suite, because that suite runs under a JIT
    test host:

    - The entrypoint alias and the root command name in usage and error output
      are derived from native argv[0]. Under `dotnet test` the entry process is
      the test host, so the name is never `clawctl`.
    - Command-line parsing, help rendering, and completion must survive
      trimming and ahead-of-time compilation. A successful publish is not
      evidence; the binary has to run.

    This script publishes the NativeAOT scenario driver in
    `tests\OpenClaw.Launcher.AotSmoke` for win-x64, copies it to `clawctl.exe`
    in an isolated directory so the native alias resolver selects the
    management entrypoint, and runs it once.

    The driver calls the same `Program.RunAsync` that the shipped `Main` calls,
    so startup diagnostics, argument routing, the operational error boundary,
    and disposal are all covered. It never runs the production `Main` adapter,
    because that adapter resolves the diagnostic log under the user's profile:
    a gate that ran it would append to the developer's real
    `%LOCALAPPDATA%\OpenClawGatewayMSIX` log. Every collaborator the driver
    injects is fixture-owned, including an explicit temporary diagnostic path
    and Node/launch delegates that cannot start a real process.

    Scenarios that require a compatible device-installed Node.js runtime are
    deliberately excluded. Those stay in the xUnit suite, where the runtime is
    injected, so this gate does not depend on the agent's installed Node
    version. The publish output lives under a temporary directory that this
    script owns and removes.

.PARAMETER Configuration
    The MSBuild configuration to publish. Defaults to Release, which matches
    continuous integration.

.EXAMPLE
    .\scripts\Test-NativeAotCli.Tests.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repositoryRoot (
    'tests\OpenClaw.Launcher.AotSmoke\OpenClaw.Launcher.AotSmoke.csproj')

if (-not (Test-Path -LiteralPath $project)) {
    throw "Could not find the NativeAOT scenario driver at '$project'."
}

$publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'clawctl-aot-' + [guid]::NewGuid().ToString('n'))

function Assert-Invocation {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [int]$ExpectedExitCode,

        [string[]]$ExpectedPatterns = @(),

        [Parameter(Mandatory)]
        [string]$Description
    )

    $output = & $Executable @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne $ExpectedExitCode) {
        throw (
            "$Description expected exit code $ExpectedExitCode but received " +
            "$exitCode. Output:`n$output"
        )
    }

    foreach ($pattern in $ExpectedPatterns) {
        if ($output -notmatch $pattern) {
            throw (
                "$Description expected output matching '$pattern'. " +
                "Output:`n$output"
            )
        }
    }

    return $output
}

try {
    Write-Host "Publishing the NativeAOT scenario driver ($Configuration)..."
    dotnet publish $project `
        --configuration $Configuration `
        --runtime win-x64 `
        -p:Platform=x64 `
        --self-contained `
        --output $publishRoot `
        --nologo `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "NativeAOT publish failed. Exit code: $LASTEXITCODE."
    }

    $driver = Join-Path $publishRoot 'OpenClaw.Launcher.AotSmoke.exe'
    if (-not (Test-Path -LiteralPath $driver)) {
        throw "The NativeAOT publish did not produce '$driver'."
    }

    # The management entrypoint is selected by executable name, so the copy is
    # how this gate reaches clawctl. It is test infrastructure and never a
    # runtime extraction step.
    $clawctl = Join-Path $publishRoot 'clawctl.exe'
    Copy-Item -LiteralPath $driver -Destination $clawctl -Force

    $output = Assert-Invocation `
        -Executable $clawctl `
        -Arguments @() `
        -ExpectedExitCode 0 `
        -ExpectedPatterns @('NativeAOT scenarios passed\.') `
        -Description 'NativeAOT scenario driver running as clawctl.exe'
    Write-Host $output

    # A gate that passes under any executable name would not be testing the
    # alias at all. Under the wrong name the driver's first scenario must fail.
    $wrongName = Join-Path $publishRoot 'notclawctl.exe'
    Copy-Item -LiteralPath $driver -Destination $wrongName -Force
    Assert-Invocation `
        -Executable $wrongName `
        -Arguments @() `
        -ExpectedExitCode 1 `
        -ExpectedPatterns @('native alias resolves to clawctl') `
        -Description 'NativeAOT scenario driver running under the wrong name'

    Write-Host 'NativeAOT clawctl checks completed successfully.'
}
finally {
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
}

# The negative check deliberately runs a native command that exits non-zero, and
# $LASTEXITCODE is still set when the script ends. GitHub Actions exits a pwsh
# step with that value, so a fully successful run would otherwise report
# failure. Any assertion failure throws instead and never reaches this line.
exit 0
