<#
.SYNOPSIS
    Installs or removes this repository's optional pre-push hook.

.DESCRIPTION
    The hook runs scripts\Test-DotNetQuality.ps1 before a push so the same
    checks continuous integration runs are reported locally first. It is
    deliberately opt in.

    Installation writes hooks\pre-push into the hooks directory Git actually
    consults for this working tree, which Git reports itself. A clone keeps one
    hooks directory, so installing or removing from a linked worktree affects
    every worktree of that clone. Other clones are unaffected.

    Nothing outside this clone changes: global Git configuration and
    core.hooksPath are never modified. If core.hooksPath is already set, the
    script stops rather than writing a hook that Git would ignore.

    A managed hook is identified by the marker comment carried in
    hooks\pre-push. Installing over a managed hook refreshes it, installing
    over an unmanaged hook fails, and removal only deletes a hook that carries
    the marker. Both operations are idempotent.

    The hook is a convenience. It is local, it is bypassable with
    `git push --no-verify`, and required CI checks remain authoritative.

.PARAMETER Remove
    Remove the managed hook instead of installing it.

.PARAMETER RepositoryRoot
    The working tree to operate on. Defaults to this repository. This exists so
    the hook tests can run against an isolated temporary repository.

.EXAMPLE
    .\scripts\Install-GitHooks.ps1

.EXAMPLE
    .\scripts\Install-GitHooks.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Remove,

    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$managedHookMarker = 'openclaw-managed-hook'
$hookName = 'pre-push'

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw "Repository root '$RepositoryRoot' does not exist."
}

$sourceHook = Join-Path $RepositoryRoot "hooks\$hookName"
if (-not (Test-Path -LiteralPath $sourceHook -PathType Leaf)) {
    throw "Could not find the tracked hook at '$sourceHook'."
}

& git -C $RepositoryRoot rev-parse --is-inside-work-tree 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "'$RepositoryRoot' is not a Git working tree."
}

$configuredHooksPath = & git -C $RepositoryRoot config --get core.hooksPath 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($configuredHooksPath)) {
    throw (
        "core.hooksPath is set to '$configuredHooksPath', so Git would ignore " +
        'a hook installed in the default hooks directory. This script does ' +
        'not change Git configuration. Clear core.hooksPath first, or install ' +
        'the hook into that directory yourself.'
    )
}

# Ask Git for the hooks directory rather than composing one. A linked
# worktree's own Git directory is not where Git looks for hooks; the clone's
# common hooks directory is, and only Git knows which that is.
$reportedHooksDirectory =
    & git -C $RepositoryRoot rev-parse --path-format=absolute --git-path hooks 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($reportedHooksDirectory)) {
    throw "Git did not report a hooks directory for '$RepositoryRoot'."
}

# Git reports forward slashes even on Windows.
$hooksDirectory = [System.IO.Path]::GetFullPath($reportedHooksDirectory)
$targetHook = Join-Path $hooksDirectory $hookName

function Test-ManagedHook {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $content = [System.IO.File]::ReadAllText($Path)
    return $content.Contains($managedHookMarker, [System.StringComparison]::Ordinal)
}

if ($Remove) {
    if (-not (Test-Path -LiteralPath $targetHook -PathType Leaf)) {
        Write-Host "No $hookName hook is installed; nothing to remove."
        return
    }

    if (-not (Test-ManagedHook -Path $targetHook)) {
        throw (
            "The existing $hookName hook was not installed by this repository " +
            "and will not be removed. Inspect '$targetHook' and delete it " +
            'yourself if you no longer want it.'
        )
    }

    Remove-Item -LiteralPath $targetHook -Force
    Write-Host "Removed the managed $hookName hook from '$targetHook'."
    Write-Host 'Every worktree of this clone stops running it.'
    return
}

if (
    (Test-Path -LiteralPath $targetHook -PathType Leaf) -and
    -not (Test-ManagedHook -Path $targetHook)
) {
    throw (
        "A $hookName hook already exists at '$targetHook' and was not " +
        'installed by this repository. It will not be overwritten. Move or ' +
        'delete it first if you want the managed hook.'
    )
}

if (-not (Test-Path -LiteralPath $hooksDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $hooksDirectory -Force | Out-Null
}

# Written rather than copied so the hook always lands with LF endings, which
# the shell that runs Git hooks requires.
$hookContent = [System.IO.File]::ReadAllText($sourceHook) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($targetHook, $hookContent)

Write-Host "Installed the $hookName hook at '$targetHook'."
Write-Host 'It runs scripts\Test-DotNetQuality.ps1 before every push.'
Write-Host 'Every worktree of this clone shares that hook.'
Write-Host 'Bypass a single push with `git push --no-verify`.'
