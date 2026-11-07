#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][string]$IsoPath,
    [Parameter(Mandatory)][string]$VhdxPath,
    [ValidateRange(1,65535)][int]$ImageIndex,
    [string]$ImageName,
    [ValidateRange(64GB,64TB)][long]$SizeBytes = 80GB
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsIsoVhdx.psm1')
Invoke-WindowsIsoVhdx @PSBoundParameters
