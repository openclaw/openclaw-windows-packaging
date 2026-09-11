<#
.SYNOPSIS
    Verifies the clawctl command line inside a published NativeAOT binary.

.DESCRIPTION
    clawctl parses its arguments with System.CommandLine. Two of its behaviors
    cannot be proven by the xUnit suite, because that suite runs under a JIT
    test host:

    - The root command name in usage and error output is derived from native
      argv[0]. Under `dotnet test` the entry process is the test host, so the
      name is never `clawctl`.
    - Command-line parsing, help rendering, and completion must survive
      trimming and ahead-of-time compilation. A successful publish is not
      evidence; the binary has to run.

    This script publishes the production launcher for win-x64 with NativeAOT,
    copies it to `clawctl.exe` in an isolated directory so the native alias
    resolver selects the management entrypoint, and asserts observable exit
    codes and output.

    Scenarios that require a compatible device-installed Node.js runtime are
    deliberately excluded. Those stay in the xUnit suite, where the runtime is
    injected, so this gate does not depend on the agent's installed Node
    version. Only the read-only, dependency-free surface is exercised here, and
    the publish output lives under a temporary directory that this script owns
    and removes.

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
$project = Join-Path $repositoryRoot 'src\OpenClaw.Launcher\OpenClaw.Launcher.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    throw "Could not find the launcher project at '$project'."
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
}

try {
    Write-Host "Publishing the launcher with NativeAOT ($Configuration)..."
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

    $launcher = Join-Path $publishRoot 'openclaw.exe'
    if (-not (Test-Path -LiteralPath $launcher)) {
        throw "The NativeAOT publish did not produce '$launcher'."
    }

    # The management entrypoint is selected by executable name, so the copy is
    # how this gate reaches clawctl. It is test infrastructure and never a
    # runtime extraction step.
    $clawctl = Join-Path $publishRoot 'clawctl.exe'
    Copy-Item -LiteralPath $launcher -Destination $clawctl -Force

    Assert-Invocation `
        -Executable $clawctl `
        -Arguments @() `
        -ExpectedExitCode 0 `
        -ExpectedPatterns @('Usage:\s*\r?\n\s*clawctl', 'setup', 'Node\.js') `
        -Description 'Bare clawctl'

    Assert-Invocation `
        -Executable $clawctl `
        -Arguments @('--help') `
        -ExpectedExitCode 0 `
        -ExpectedPatterns @('clawctl', 'setup', '--version') `
        -Description 'clawctl --help'

    Assert-Invocation `
        -Executable $clawctl `
        -Arguments @('setup', '--help') `
        -ExpectedExitCode 0 `
        -ExpectedPatterns @('clawctl setup') `
        -Description 'clawctl setup --help'

    # Assembly version, not the informational version, and not the host's.
    Assert-Invocation `
        -Executable $clawctl `
        -Arguments @('--version') `
        -ExpectedExitCode 0 `
        -ExpectedPatterns @('^\s*\d+\.\d+\.\d+\.\d+\s*$') `
        -Description 'clawctl --version'

    Assert-Invocation `
        -Executable $clawctl `
        -Arguments @('bogus') `
        -ExpectedExitCode 1 `
        -ExpectedPatterns @("'bogus'") `
        -Description 'clawctl with an unknown command'

    # Response-file expansion is disabled, so `@file` is an ordinary token and
    # must be rejected rather than read from disk.
    $responseFile = Join-Path $publishRoot 'help.rsp'
    Set-Content -LiteralPath $responseFile -Value '--help' -Encoding utf8
    Assert-Invocation `
        -Executable $clawctl `
        -Arguments @("@$responseFile") `
        -ExpectedExitCode 1 `
        -Description 'clawctl with a response-file token'

    Write-Host 'NativeAOT clawctl checks completed successfully.'
}
finally {
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
}
