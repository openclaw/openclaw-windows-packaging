[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('mxc-metadata-fixture-'+[guid]::NewGuid().ToString('N'))
$package=Join-Path $root 'package'
$scriptPath=Join-Path $PSScriptRoot 'Get-MxcSdkMetadata.ps1'
[xml]$central=Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Packages.props') -Raw
$version=[string]$central.SelectSingleNode('//PackageVersion[@Include="Microsoft.Mxc.Sdk"]').Version
try{
    $null=New-Item -ItemType Directory -Path $package
    [IO.File]::WriteAllText((Join-Path $package 'Microsoft.Mxc.Sdk.nuspec'),"<package><metadata><id>Microsoft.Mxc.Sdk</id><version>$version</version></metadata></package>")
    $notice="Fixture copyright`nFixture permission notice`n"
    [IO.File]::WriteAllText((Join-Path $package 'LICENSE.md'),$notice)
    foreach($arch in @('x64','arm64')){
        $output=Join-Path $root $arch
        $result=& $scriptPath -Architecture $arch -PackageDirectory $package -OutputDirectory $output
        $metadata=Get-Content -LiteralPath (Join-Path $output 'mxc-runtime.json') -Raw|ConvertFrom-Json
        if($metadata.package -cne 'Microsoft.Mxc.Sdk' -or $metadata.version -cne $version -or $metadata.architecture -cne $arch){
            throw 'Staged API dependency metadata does not describe the restored package.'
        }
        if([IO.File]::ReadAllText((Join-Path $output 'mxc-LICENSE.md')) -cne $notice -or @($result.Files).Count -ne 2){
            throw 'The complete redistribution notice was not preserved.'
        }
    }
    Remove-Item -LiteralPath (Join-Path $package 'LICENSE.md')
    $failure=$null
    try{& $scriptPath -Architecture x64 -PackageDirectory $package -OutputDirectory (Join-Path $root 'missing-notice')|Out-Null}catch{$failure=$_}
    if($null -eq $failure -or $failure.Exception.Message -notmatch 'redistribution license is missing'){throw 'A missing legal notice was accepted.'}
    [IO.File]::WriteAllText((Join-Path $package 'LICENSE.md'),$notice)
    [IO.File]::WriteAllText((Join-Path $package 'Microsoft.Mxc.Sdk.nuspec'),'<package><metadata><id>Microsoft.Mxc.Sdk</id><version>0.0.0</version></metadata></package>')
    $failure=$null
    try{& $scriptPath -Architecture x64 -PackageDirectory $package -OutputDirectory (Join-Path $root 'wrong-package')|Out-Null}catch{$failure=$_}
    if($null -eq $failure -or $failure.Exception.Message -notmatch 'central package selection'){throw 'Stale SDK package metadata was accepted.'}
    'PASS: SDK API metadata and legal notice stage without native-file knowledge; missing notice/stale package rejected.'
}finally{
    if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}
}
