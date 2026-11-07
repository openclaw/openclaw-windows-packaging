#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$VhdxPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$CrabboxPath,
    [Parameter(Mandatory)][string]$Switch,
    [string]$AdminUser = 'crabbox',
    [Security.SecureString]$AdminPassword,
    [switch]$Prime,
    [ValidateRange(1,60)][int]$PrimeTimeoutMinutes = 25
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsTemplate.psm1')
Initialize-CrabboxWindowsTemplate @PSBoundParameters
