#Requires -Version 7.0

<#
.SYNOPSIS
    Checks native staging against an existing EFS-encrypted application fixture.

.DESCRIPTION
    Reads the fixture without setting up EFS. Staged files must be unencrypted
    and byte-identical. Requires an unencrypted temporary directory.

.PARAMETER ApplicationDirectory
    Application with an EFS-encrypted file in a native-bearing node_modules package.

.EXAMPLE
    .\scripts\Test-SessionNativeStagerEfs.Tests.ps1 -ApplicationDirectory C:\fixtures\efs-native\app
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ApplicationDirectory
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'This regression requires Windows and an existing EFS-encrypted fixture.'
}

$application = (Resolve-Path -LiteralPath $ApplicationDirectory).ProviderPath
$temporaryDirectory = [IO.Path]::GetTempPath()
if ([IO.File]::GetAttributes($temporaryDirectory) -band [IO.FileAttributes]::Encrypted) {
    throw 'The temporary directory must not be EFS-encrypted.'
}

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$hostProject = [Security.SecurityElement]::Escape(
    (Join-Path $repositoryRoot 'src\OpenClaw.SessionHost\OpenClaw.SessionHost.csproj'))
$root = Join-Path $temporaryDirectory "openclaw-efs-tests-$([Guid]::NewGuid().ToString('N'))"
$project = Join-Path $root 'EfsStaging.csproj'
$passed = $false
[IO.Directory]::CreateDirectory($root) | Out-Null

try {
    [IO.File]::WriteAllText($project, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <AssemblyName>OpenClaw.Launcher.Tests</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$hostProject" />
  </ItemGroup>
</Project>
"@)
    [IO.File]::WriteAllText((Join-Path $root 'Program.cs'), @'
using System.Security.Cryptography;
using OpenClaw.SessionHost;

string application = args[0];
string modules = Path.Combine(application, "node_modules");
string[] sources = SessionNativeStager.DiscoverNativePackages(modules)
    .SelectMany(package => Directory.EnumerateFiles(
        Path.Combine(modules, package), "*", SearchOption.AllDirectories))
    .ToArray();
if (!sources.Any(path => File.GetAttributes(path).HasFlag(FileAttributes.Encrypted)))
{
    throw new InvalidOperationException(
        "The fixture must contain an EFS-encrypted file in a native-bearing package.");
}

Dictionary<string, byte[]> hashes = sources.ToDictionary(path => path, HashFile);
string staged = SessionNativeStager.Stage(application, args[1])
    ?? throw new InvalidOperationException("No native packages were staged.");
foreach ((string source, byte[] expected) in hashes)
{
    string copy = Path.Combine(staged, "node_modules", Path.GetRelativePath(modules, source));
    if (File.GetAttributes(copy).HasFlag(FileAttributes.Encrypted))
    {
        throw new InvalidOperationException($"The staged file retained EFS encryption: {copy}");
    }

    if (!expected.AsSpan().SequenceEqual(HashFile(copy)))
    {
        throw new InvalidOperationException($"The staged bytes differ from the source: {copy}");
    }
}

using FileStream lease = SessionNativeStager.OpenConsumerLease(staged)
    ?? throw new InvalidOperationException("The staged root has no completion marker.");
if (SessionNativeStager.Stage(application, args[1]) != staged)
{
    throw new InvalidOperationException("Unchanged content did not reuse the staged root.");
}

Console.WriteLine($"EFS native staging passed: {hashes.Count} files.");

static byte[] HashFile(string path)
{
    using FileStream input = File.OpenRead(path);
    return SHA256.HashData(input);
}
'@)

    & dotnet run --project $project --configuration Release --verbosity quiet -- `
        $application (Join-Path $root 'local')
    if ($LASTEXITCODE -ne 0) {
        throw "EFS staging regression failed. Exit code: $LASTEXITCODE."
    }
    $passed = $true
}
finally {
    # Preserve a test failure if cleanup also fails.
    Remove-Item -LiteralPath $root -Recurse -Force `
        -ErrorAction $(if ($passed) { 'Stop' } else { 'Continue' })
}
