<#
.SYNOPSIS
    Tests the optional pre-push hook and its installer.

.DESCRIPTION
    Every case runs against a throwaway Git repository under the temporary
    directory. Nothing here touches this clone's .git directory, the user's
    global Git configuration, or the real quality script.

    The end-to-end cases replace scripts\Test-DotNetQuality.ps1 in the
    throwaway repository with a stub, then perform a real `git push` to a local
    bare remote. That proves Git actually invokes the hook and that the hook's
    exit code decides whether the push proceeds, without running a full build.

    Every case runs twice: once in an ordinary clone and once against a linked
    worktree of that clone, because Git keeps one hooks directory per clone but
    runs the pushing worktree's quality script.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$installScript = Join-Path $PSScriptRoot 'Install-GitHooks.ps1'
$trackedHook = Join-Path $repositoryRoot 'hooks\pre-push'

function Invoke-Git {
    param(
        [Parameter(Mandatory, ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $output"
    }

    return $output
}

function New-QualityStub {
    param(
        [Parameter(Mandatory)]
        [int]$ExitCode,

        [Parameter(Mandatory)]
        [string]$Label
    )

    return "Write-Host 'stub quality check ($Label)'`nexit $ExitCode`n"
}

function New-TestRepository {
    <#
        Creates a work tree wired to a local bare remote, seeded with the
        tracked hook and a stub quality script that exits with $StubExitCode.

        With -Layout Worktree the repository also gains a linked worktree on
        its own branch, and that linked worktree becomes the target the case
        operates on. The primary work tree then carries the opposite stub exit
        code, so a push from the linked worktree can only behave as the case
        expects if Git ran the pushing worktree's quality script.
    #>
    param(
        [int]$StubExitCode = 0,

        [ValidateSet('Clone', 'Worktree')]
        [string]$Layout = 'Clone'
    )

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('n'))
    $work = Join-Path $root 'work'
    $remote = Join-Path $root 'remote.git'
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    Invoke-Git init --bare --initial-branch=main $remote | Out-Null
    Invoke-Git init --initial-branch=main $work | Out-Null
    Invoke-Git -C $work config user.email 'hook-tests@example.invalid' | Out-Null
    Invoke-Git -C $work config user.name 'Hook Tests' | Out-Null
    Invoke-Git -C $work remote add origin $remote | Out-Null

    New-Item -ItemType Directory -Path (Join-Path $work 'hooks') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $work 'scripts') -Force | Out-Null

    $hookContent = [System.IO.File]::ReadAllText($trackedHook) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText((Join-Path $work 'hooks\pre-push'), $hookContent)

    $primaryExitCode = $StubExitCode
    if ($Layout -eq 'Worktree') {
        $primaryExitCode = if ($StubExitCode -eq 0) { 1 } else { 0 }
    }

    [System.IO.File]::WriteAllText(
        (Join-Path $work 'scripts\Test-DotNetQuality.ps1'),
        (New-QualityStub -ExitCode $primaryExitCode -Label 'primary'))

    [System.IO.File]::WriteAllText((Join-Path $work 'seed.txt'), "seed`n")
    Invoke-Git -C $work add --all | Out-Null
    Invoke-Git -C $work commit -m 'seed' | Out-Null

    $target = $work
    $branch = 'main'
    if ($Layout -eq 'Worktree') {
        $linked = Join-Path $root 'linked'
        Invoke-Git -C $work worktree add -b feature $linked | Out-Null
        [System.IO.File]::WriteAllText(
            (Join-Path $linked 'scripts\Test-DotNetQuality.ps1'),
            (New-QualityStub -ExitCode $StubExitCode -Label 'linked'))
        Invoke-Git -C $linked add --all | Out-Null
        Invoke-Git -C $linked commit -m 'linked stub' | Out-Null

        $target = $linked
        $branch = 'feature'
    }

    return [pscustomobject]@{
        Root      = $root
        Work      = $work
        Remote    = $remote
        Layout    = $Layout
        Target    = $target
        Branch    = $branch

        # The clone's common hooks directory, which Git consults for every
        # worktree. Stated independently of the installer on purpose.
        HookPath  = Join-Path $work '.git\hooks\pre-push'
    }
}

function Assert-Fails {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$MessagePattern,

        [Parameter(Mandatory)]
        [string]$Case
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw (
                "$Case`: expected a failure matching '$MessagePattern'; " +
                "received: $($_.Exception.Message)"
            )
        }
        return
    }

    throw "$Case`: expected a failure matching '$MessagePattern', but it succeeded."
}

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-Case {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Body,

        [int]$StubExitCode = 0,

        [ValidateSet('Clone', 'Worktree')]
        [string[]]$Layouts = @('Clone', 'Worktree')
    )

    foreach ($layout in $Layouts) {
        $repository = New-TestRepository -StubExitCode $StubExitCode -Layout $layout
        try {
            & $Body $repository
            Write-Host "PASS $Name [$layout]"
        }
        finally {
            Remove-Item -LiteralPath $repository.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Invoke-Case 'installs the managed hook' {
    param($repository)

    & $installScript -RepositoryRoot $repository.Target | Out-Null

    Assert-True `
        -Condition (Test-Path -LiteralPath $repository.HookPath -PathType Leaf) `
        -Message 'Install did not create .git\hooks\pre-push.'

    $installed = [System.IO.File]::ReadAllText($repository.HookPath)
    Assert-True `
        -Condition $installed.Contains('openclaw-managed-hook') `
        -Message 'The installed hook is missing its managed marker.'
    Assert-True `
        -Condition (-not $installed.Contains("`r`n")) `
        -Message 'The installed hook must use LF line endings.'
}

Invoke-Case 'installing twice is idempotent' {
    param($repository)

    & $installScript -RepositoryRoot $repository.Target | Out-Null
    & $installScript -RepositoryRoot $repository.Target | Out-Null

    Assert-True `
        -Condition (Test-Path -LiteralPath $repository.HookPath -PathType Leaf) `
        -Message 'The second install removed the hook.'
}

Invoke-Case 'a passing check allows the push' {
    param($repository)

    & $installScript -RepositoryRoot $repository.Target | Out-Null
    Invoke-Git -C $repository.Target push origin $($repository.Branch) | Out-Null

    $remoteHead = Invoke-Git --git-dir=$($repository.Remote) rev-parse $($repository.Branch)
    $localHead = Invoke-Git -C $repository.Target rev-parse $($repository.Branch)
    Assert-True `
        -Condition ($remoteHead -eq $localHead) `
        -Message 'The push did not reach the remote.'
}

Invoke-Case 'a failing check blocks the push' -StubExitCode 1 {
    param($repository)

    & $installScript -RepositoryRoot $repository.Target | Out-Null

    Assert-Fails `
        -Case 'failing check' `
        -MessagePattern 'failed' `
        -Action { Invoke-Git -C $repository.Target push origin $($repository.Branch) }

    & git --git-dir=$($repository.Remote) rev-parse --verify $($repository.Branch) 2>&1 | Out-Null
    Assert-True `
        -Condition ($LASTEXITCODE -ne 0) `
        -Message 'A blocked push still updated the remote.'
}

Invoke-Case 'a failing check is bypassable with --no-verify' -StubExitCode 1 {
    param($repository)

    & $installScript -RepositoryRoot $repository.Target | Out-Null
    Invoke-Git -C $repository.Target push --no-verify origin $($repository.Branch) | Out-Null

    $remoteHead = Invoke-Git --git-dir=$($repository.Remote) rev-parse $($repository.Branch)
    Assert-True `
        -Condition (-not [string]::IsNullOrWhiteSpace($remoteHead)) `
        -Message '--no-verify did not bypass the failing hook.'
}

Invoke-Case 'removes the managed hook' {
    param($repository)

    & $installScript -RepositoryRoot $repository.Target | Out-Null
    & $installScript -RepositoryRoot $repository.Target -Remove | Out-Null

    Assert-True `
        -Condition (-not (Test-Path -LiteralPath $repository.HookPath)) `
        -Message 'Remove did not delete the managed hook.'
}

Invoke-Case 'removing twice is idempotent' {
    param($repository)

    & $installScript -RepositoryRoot $repository.Target | Out-Null
    & $installScript -RepositoryRoot $repository.Target -Remove | Out-Null
    & $installScript -RepositoryRoot $repository.Target -Remove | Out-Null
}

Invoke-Case 'refuses to overwrite an unmanaged hook' {
    param($repository)

    $hooksDirectory = Split-Path $repository.HookPath -Parent
    New-Item -ItemType Directory -Path $hooksDirectory -Force | Out-Null
    [System.IO.File]::WriteAllText($repository.HookPath, "#!/bin/sh`necho mine`n")

    Assert-Fails `
        -Case 'unmanaged install' `
        -MessagePattern 'not be overwritten' `
        -Action { & $installScript -RepositoryRoot $repository.Target }

    $preserved = [System.IO.File]::ReadAllText($repository.HookPath)
    Assert-True `
        -Condition $preserved.Contains('echo mine') `
        -Message 'The unmanaged hook was modified.'
}

Invoke-Case 'refuses to remove an unmanaged hook' {
    param($repository)

    $hooksDirectory = Split-Path $repository.HookPath -Parent
    New-Item -ItemType Directory -Path $hooksDirectory -Force | Out-Null
    [System.IO.File]::WriteAllText($repository.HookPath, "#!/bin/sh`necho mine`n")

    Assert-Fails `
        -Case 'unmanaged remove' `
        -MessagePattern 'will not be removed' `
        -Action { & $installScript -RepositoryRoot $repository.Target -Remove }

    Assert-True `
        -Condition (Test-Path -LiteralPath $repository.HookPath -PathType Leaf) `
        -Message 'The unmanaged hook was deleted.'
}

Invoke-Case 'stops when core.hooksPath is set' {
    param($repository)

    Invoke-Git -C $repository.Target config core.hooksPath 'custom-hooks' | Out-Null

    Assert-Fails `
        -Case 'core.hooksPath' `
        -MessagePattern 'core\.hooksPath' `
        -Action { & $installScript -RepositoryRoot $repository.Target }
}

Invoke-Case 'installs into the clone-wide hooks directory' -Layouts 'Worktree' {
    param($repository)

    & $installScript -RepositoryRoot $repository.Target | Out-Null

    Assert-True `
        -Condition (Test-Path -LiteralPath $repository.HookPath -PathType Leaf) `
        -Message 'Installing from a linked worktree did not reach the shared hooks directory.'

    $privateHooks = Get-ChildItem `
        -Path (Join-Path $repository.Work '.git\worktrees') `
        -Recurse -Filter 'pre-push' -File -ErrorAction SilentlyContinue
    Assert-True `
        -Condition ($null -eq $privateHooks) `
        -Message 'The hook was written into a private worktree directory Git does not consult.'

    # The primary work tree sees the same hook, so installing again is a no-op
    # rather than a second installation.
    & $installScript -RepositoryRoot $repository.Work | Out-Null

    Assert-True `
        -Condition (Test-Path -LiteralPath $repository.HookPath -PathType Leaf) `
        -Message 'Reinstalling from another worktree removed the shared hook.'
}

Invoke-Case 'removal from one worktree applies to the whole clone' -StubExitCode 1 -Layouts 'Worktree' {
    param($repository)

    & $installScript -RepositoryRoot $repository.Target | Out-Null

    Assert-Fails `
        -Case 'shared hook before removal' `
        -MessagePattern 'failed' `
        -Action { Invoke-Git -C $repository.Target push origin $($repository.Branch) }

    & $installScript -RepositoryRoot $repository.Work -Remove | Out-Null

    Assert-True `
        -Condition (-not (Test-Path -LiteralPath $repository.HookPath)) `
        -Message 'Removing from the primary work tree left the shared hook in place.'

    Invoke-Git -C $repository.Target push origin $($repository.Branch) | Out-Null

    $remoteHead = Invoke-Git --git-dir=$($repository.Remote) rev-parse $($repository.Branch)
    Assert-True `
        -Condition (-not [string]::IsNullOrWhiteSpace($remoteHead)) `
        -Message 'The linked worktree still ran a hook after it was removed.'
}

Write-Host 'All Git hook tests passed.'
