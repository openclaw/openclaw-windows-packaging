<#
.SYNOPSIS
    Tests the optional pre-push hook and its installer.

.DESCRIPTION
    The suite creates reusable throwaway Git repository templates under one
    suite-owned temporary root. Each case receives a copied template, so no
    case touches this clone's .git directory, the user's global Git
    configuration, another case, or the real quality script.

    The end-to-end cases replace scripts\Test-DotNetQuality.ps1 in the
    throwaway repository with a stub, then perform a real `git push` to a local
    bare remote. That proves Git actually invokes the hook and that the hook's
    exit code decides whether the push proceeds, without running a full build.

    The documentation-skip cases start from a template whose branch is already
    on the remote and whose stub fails. A push that succeeds without the stub's
    output proves the hook skipped the quality script; a blocked push that
    printed the pushing worktree's stub label proves it ran.

    Cases run in an ordinary clone and against a linked worktree of that
    clone, because Git keeps one hooks directory per clone but runs the pushing
    worktree's quality script. Cases whose behavior does not depend on the
    layout run in the clone only.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$installScript = Join-Path $PSScriptRoot 'Install-GitHooks.ps1'
$trackedHook = Join-Path $repositoryRoot 'hooks\pre-push'
$packagingRelevanceScript = Join-Path $PSScriptRoot 'Get-PackagingRelevance.ps1'
$suiteRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "openclaw-git-hooks-$([System.Guid]::NewGuid().ToString('n'))")
$templatesRoot = Join-Path $suiteRoot 'templates'
$casesRoot = Join-Path $suiteRoot 'cases'
$testRepositoryTemplates = @{}
$templateRemoteRefs = @{}

New-Item -ItemType Directory -Path $templatesRoot, $casesRoot -Force | Out-Null

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

function Get-TestRepositoryTemplate {
    <#
        Creates a work tree wired to a local bare remote, seeded with the
        tracked hook and a stub quality script that exits with $StubExitCode.

        With -Layout Worktree the repository also gains a linked worktree on
        its own branch, and that linked worktree becomes the target the case
        operates on. The primary work tree then carries the opposite stub exit
        code, so a push from the linked worktree can only behave as the case
        expects if Git ran the pushing worktree's quality script.

        With -Published the target branch is pushed to the remote without a
        hook, and the managed hook is then installed. That is the starting
        point for cases that classify what a later push changes.
    #>
    param(
        [int]$StubExitCode = 0,

        [ValidateSet('Clone', 'Worktree')]
        [string]$Layout = 'Clone',

        [switch]$Published
    )

    $key = "$Layout/$StubExitCode/$Published"
    if ($testRepositoryTemplates.ContainsKey($key)) {
        return $testRepositoryTemplates[$key]
    }

    $root = Join-Path $templatesRoot ([System.Guid]::NewGuid().ToString('n'))
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
    $relevanceContent = [System.IO.File]::ReadAllText($packagingRelevanceScript) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText(
        (Join-Path $work 'scripts\Get-PackagingRelevance.ps1'),
        $relevanceContent)

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

    if ($Published) {
        Invoke-Git -C $target push origin $branch | Out-Null
        & $installScript -RepositoryRoot $target | Out-Null
    }

    $template = [pscustomobject]@{
        Root      = $root
        Work      = $work
        Remote    = $remote
        Layout    = $Layout
        Target    = $target
        Branch    = $branch

        # The clone's common hooks directory, which Git consults for every
        # worktree. Stated independently of the installer on purpose.
        HookPath  = Join-Path $work '.git\hooks\pre-push'
        Label     = if ($Layout -eq 'Worktree') { 'linked' } else { 'primary' }
    }

    $templateRemoteRefs[$key] = @(Invoke-Git --git-dir=$remote for-each-ref)
    $testRepositoryTemplates[$key] = $template
    return $template
}

function Update-TemplatePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$TemplateRoot,

        [Parameter(Mandatory)]
        [string]$CaseRoot
    )

    $content = [System.IO.File]::ReadAllText($Path)
    $rewritten = $content.Replace($TemplateRoot, $CaseRoot).Replace(
        $TemplateRoot.Replace('\', '\\'),
        $CaseRoot.Replace('\', '\\')).Replace(
        $TemplateRoot.Replace('\', '/'),
        $CaseRoot.Replace('\', '/'))
    [System.IO.File]::SetAttributes($Path, [System.IO.FileAttributes]::Normal)
    [System.IO.File]::WriteAllText($Path, $rewritten)

    Assert-True `
        -Condition (
            -not $rewritten.Contains($TemplateRoot) -and
            -not $rewritten.Contains($TemplateRoot.Replace('\', '\\')) -and
            -not $rewritten.Contains($TemplateRoot.Replace('\', '/'))) `
        -Message "The copied Git metadata still points at template '$TemplateRoot'."
}

function New-TestRepository {
    param(
        [int]$StubExitCode = 0,

        [ValidateSet('Clone', 'Worktree')]
        [string]$Layout = 'Clone',

        [switch]$Published
    )

    $template = Get-TestRepositoryTemplate `
        -StubExitCode $StubExitCode `
        -Layout $Layout `
        -Published:$Published
    $root = Join-Path $casesRoot ([System.Guid]::NewGuid().ToString('n'))

    Copy-Item -LiteralPath $template.Root -Destination $root -Recurse -Force

    $work = Join-Path $root 'work'
    $remote = Join-Path $root 'remote.git'
    Update-TemplatePath `
        -Path (Join-Path $work '.git\config') `
        -TemplateRoot $template.Root `
        -CaseRoot $root
    $remoteConfig = [System.IO.File]::ReadAllText((Join-Path $work '.git\config'))
    Assert-True `
        -Condition ($remoteConfig.Contains($remote.Replace('\', '\\')) -or
            $remoteConfig.Contains($remote.Replace('\', '/'))) `
        -Message 'The copied Git remote does not point at the case remote.'

    if ($Layout -eq 'Worktree') {
        Update-TemplatePath `
            -Path (Join-Path $root 'linked\.git') `
            -TemplateRoot $template.Root `
            -CaseRoot $root
        Update-TemplatePath `
            -Path (Join-Path $work '.git\worktrees\linked\gitdir') `
            -TemplateRoot $template.Root `
            -CaseRoot $root
    }

    $target = if ($Layout -eq 'Worktree') { Join-Path $root 'linked' } else { $work }
    return [pscustomobject]@{
        Root     = $root
        Work     = $work
        Remote   = $remote
        Layout   = $Layout
        Target   = $target
        Branch   = if ($Layout -eq 'Worktree') { 'feature' } else { 'main' }
        HookPath = Join-Path $work '.git\hooks\pre-push'
        Label    = if ($Layout -eq 'Worktree') { 'linked' } else { 'primary' }
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
        [string[]]$Layouts = @('Clone', 'Worktree'),

        [switch]$Published
    )

    foreach ($layout in $Layouts) {
        $repository = New-TestRepository `
            -StubExitCode $StubExitCode `
            -Layout $layout `
            -Published:$Published
        try {
            & $Body $repository
            Write-Host "PASS $Name [$layout]"
        }
        finally {
            Remove-Item -LiteralPath $repository.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Add-CommittedFile {
    param(
        [Parameter(Mandatory)]
        $Repository,

        [Parameter(Mandatory)]
        [string[]]$Path
    )

    foreach ($file in $Path) {
        $fullPath = Join-Path $Repository.Target $file
        New-Item -ItemType Directory -Path (Split-Path $fullPath -Parent) -Force | Out-Null
        [System.IO.File]::WriteAllText($fullPath, "$file`n")
    }
    Invoke-Git -C $Repository.Target add -- @Path | Out-Null
    Invoke-Git -C $Repository.Target commit -m "change $($Path -join ', ')" | Out-Null
}

function Invoke-Push {
    param(
        [Parameter(Mandatory)]
        $Repository,

        [Parameter(Mandatory, ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    $output = & git -C $Repository.Target push @Arguments 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = ($output | ForEach-Object { "$_" }) -join "`n"
    }
}

function Invoke-HookDirectly {
    <#
        Runs the installed hook the way Git would, but with caller-chosen
        standard input, so cases can present input a real push never sends.
    #>
    param(
        [Parameter(Mandatory)]
        $Repository,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$HookInput
    )

    $inputPath = Join-Path $Repository.Root 'hook-input.txt'
    [System.IO.File]::WriteAllText($inputPath, $HookInput)
    $shell = Invoke-Git var GIT_SHELL_PATH
    $output = & $shell -c 'cd "$1" && exec sh "$2" origin "$3" < "$4"' `
        hook-test `
        ($Repository.Target -replace '\\', '/') `
        ($Repository.HookPath -replace '\\', '/') `
        ($Repository.Remote -replace '\\', '/') `
        ($inputPath -replace '\\', '/') 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = ($output | ForEach-Object { "$_" }) -join "`n"
    }
}

function Assert-QualityCheckSkipped {
    param(
        [Parameter(Mandatory)]
        $Result,

        [Parameter(Mandatory)]
        [string]$Reason,

        [Parameter(Mandatory)]
        [string]$Case
    )

    Assert-True `
        -Condition ($Result.ExitCode -eq 0) `
        -Message "$Case`: the push failed. Output: $($Result.Output)"
    Assert-True `
        -Condition $Result.Output.Contains(
            "pre-push: $Reason; skipping scripts/Test-DotNetQuality.ps1") `
        -Message "$Case`: the hook did not report the skip. Output: $($Result.Output)"
    Assert-True `
        -Condition (-not $Result.Output.Contains('stub quality check')) `
        -Message "$Case`: the quality script ran. Output: $($Result.Output)"
}

function Assert-QualityCheckRan {
    param(
        [Parameter(Mandatory)]
        $Result,

        [Parameter(Mandatory)]
        $Repository,

        [Parameter(Mandatory)]
        [string]$Reason,

        [Parameter(Mandatory)]
        [string]$Case
    )

    Assert-True `
        -Condition ($Result.ExitCode -ne 0) `
        -Message "$Case`: the failing quality script did not block the push. Output: $($Result.Output)"
    Assert-True `
        -Condition $Result.Output.Contains(
            "pre-push: $Reason; running scripts/Test-DotNetQuality.ps1") `
        -Message "$Case`: the hook did not report why it ran. Output: $($Result.Output)"
    Assert-True `
        -Condition $Result.Output.Contains("stub quality check ($($Repository.Label))") `
        -Message "$Case`: the pushing worktree's quality script did not run. Output: $($Result.Output)"
}

function Test-RemoteRef {
    param(
        [Parameter(Mandatory)]
        $Repository,

        [Parameter(Mandatory)]
        [string]$Ref
    )

    & git --git-dir=$($Repository.Remote) rev-parse --verify --quiet $Ref 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

try {
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

Invoke-Case 'a documentation-only push skips the quality check' -StubExitCode 1 -Published {
    param($repository)

    Add-CommittedFile $repository 'README.md', 'docs/guide/setup.md', 'LICENSE'

    $result = Invoke-Push $repository origin $repository.Branch
    Assert-QualityCheckSkipped $result 'the push changes only documentation' 'documentation-only push'

    $remoteHead = Invoke-Git --git-dir=$($repository.Remote) rev-parse $($repository.Branch)
    $localHead = Invoke-Git -C $repository.Target rev-parse HEAD
    Assert-True `
        -Condition ($remoteHead -eq $localHead) `
        -Message 'The skipped documentation push did not reach the remote.'
}

Invoke-Case 'a push that changes code runs the quality check' -StubExitCode 1 -Published {
    param($repository)

    $publishedHead = Invoke-Git --git-dir=$($repository.Remote) rev-parse $($repository.Branch)
    Add-CommittedFile $repository 'src/App.cs'
    Add-CommittedFile $repository 'README.md'

    $result = Invoke-Push $repository origin $repository.Branch
    Assert-QualityCheckRan $result $repository 'the push changes more than documentation' 'documentation and code push'

    $remoteHead = Invoke-Git --git-dir=$($repository.Remote) rev-parse $($repository.Branch)
    Assert-True `
        -Condition ($remoteHead -eq $publishedHead) `
        -Message 'A blocked push still updated the remote.'
}

Invoke-Case 'a new branch with only documentation commits skips the quality check' -StubExitCode 1 -Published {
    param($repository)

    Add-CommittedFile $repository 'docs/new-page.md'

    $result = Invoke-Push $repository origin 'HEAD:refs/heads/docs-branch'
    Assert-QualityCheckSkipped $result 'the push changes only documentation' 'new documentation branch'
    Assert-True `
        -Condition (Test-RemoteRef $repository 'refs/heads/docs-branch') `
        -Message 'The skipped new branch did not reach the remote.'
}

Invoke-Case 'a new branch with a code commit runs the quality check' -StubExitCode 1 -Published {
    param($repository)

    Add-CommittedFile $repository 'src/App.cs'

    $result = Invoke-Push $repository origin 'HEAD:refs/heads/code-branch'
    Assert-QualityCheckRan $result $repository 'the push changes more than documentation' 'new code branch'
    Assert-True `
        -Condition (-not (Test-RemoteRef $repository 'refs/heads/code-branch')) `
        -Message 'A blocked new branch still reached the remote.'
}

Invoke-Case 'deleting a remote branch skips the quality check' -StubExitCode 1 -Published -Layouts 'Clone' {
    param($repository)

    Invoke-Git -C $repository.Target push --no-verify origin 'HEAD:refs/heads/topic' | Out-Null

    $result = Invoke-Push $repository origin ':refs/heads/topic'
    Assert-QualityCheckSkipped $result 'the push only deletes remote refs' 'branch deletion'
    Assert-True `
        -Condition (-not (Test-RemoteRef $repository 'refs/heads/topic')) `
        -Message 'The branch deletion did not reach the remote.'
}

Invoke-Case 'a multi-ref push runs the quality check when any ref changes code' -StubExitCode 1 -Published -Layouts 'Clone' {
    param($repository)

    Add-CommittedFile $repository 'docs/first.md'
    $documentationCommit = Invoke-Git -C $repository.Target rev-parse HEAD
    Add-CommittedFile $repository 'src/App.cs'
    $codeCommit = Invoke-Git -C $repository.Target rev-parse HEAD

    $result = Invoke-Push $repository origin `
        "$($documentationCommit):refs/heads/a-docs" `
        "$($codeCommit):refs/heads/b-code" `
        "$($documentationCommit):refs/heads/c-docs"
    Assert-QualityCheckRan $result $repository 'the push changes more than documentation' 'multi-ref push'
    foreach ($ref in 'refs/heads/a-docs', 'refs/heads/b-code', 'refs/heads/c-docs') {
        Assert-True `
            -Condition (-not (Test-RemoteRef $repository $ref)) `
            -Message "A blocked multi-ref push still created '$ref'."
    }
}

Invoke-Case 'renaming a code file into docs runs the quality check' -StubExitCode 1 -Published -Layouts 'Clone' {
    param($repository)

    New-Item -ItemType Directory -Path (Join-Path $repository.Target 'docs') -Force | Out-Null
    Invoke-Git -C $repository.Target mv seed.txt docs/seed.md | Out-Null
    Invoke-Git -C $repository.Target commit -m 'move seed into docs' | Out-Null

    $result = Invoke-Push $repository origin $repository.Branch
    Assert-QualityCheckRan $result $repository 'the push changes more than documentation' 'rename out of code'
}

Invoke-Case 'an unclassifiable push runs the quality check' -StubExitCode 1 -Published -Layouts 'Clone' {
    param($repository)

    Add-CommittedFile $repository 'README.md'
    $classifier = Join-Path $repository.Target 'scripts\Get-PackagingRelevance.ps1'

    foreach ($replacement in @(
            $null,
            "throw 'classifier failed'`n",
            "Write-Output 'maybe'`n"
        )) {
        if ($null -eq $replacement) {
            Remove-Item -LiteralPath $classifier -Force
        }
        else {
            [System.IO.File]::WriteAllText($classifier, $replacement)
        }

        $result = Invoke-Push $repository origin $repository.Branch
        Assert-QualityCheckRan $result $repository 'could not classify the pushed files' 'unclassifiable push'
    }
}

Invoke-Case 'a documentation-only root commit runs the quality check' -StubExitCode 1 -Published -Layouts 'Clone' {
    param($repository)

    $blob = Invoke-Git -C $repository.Target rev-parse 'HEAD:seed.txt'
    $tree = "100644 blob $blob`tREADME.md" | & git -C $repository.Target mktree
    Assert-True -Condition ($LASTEXITCODE -eq 0) -Message 'git mktree failed.'
    $rootCommit = Invoke-Git -C $repository.Target commit-tree $tree -m 'documentation root'

    $result = Invoke-Push $repository origin "$($rootCommit):refs/heads/docs-root"
    Assert-QualityCheckRan $result $repository 'the push introduces a root commit' 'documentation root commit'
}

Invoke-Case 'a documentation branch that merges code runs the quality check' -StubExitCode 1 -Published -Layouts 'Clone' {
    param($repository)

    Invoke-Git -C $repository.Target switch -c code | Out-Null
    Add-CommittedFile $repository 'src/App.cs'
    Invoke-Git -C $repository.Target push --no-verify origin code | Out-Null

    Invoke-Git -C $repository.Target switch -c docs main | Out-Null
    Add-CommittedFile $repository 'docs/merge.md'
    Invoke-Git -C $repository.Target merge --no-ff --no-edit code | Out-Null

    $result = Invoke-Push $repository origin docs
    Assert-QualityCheckRan $result $repository 'the push changes more than documentation' 'merge of code into documentation'
}

Invoke-Case 'unexpected hook input runs the quality check' -StubExitCode 1 -Published -Layouts 'Clone' {
    param($repository)

    $head = Invoke-Git -C $repository.Target rev-parse HEAD
    $zero = '0' * 40

    foreach ($hookInput in @(
            '',
            "refs/heads/main $head refs/heads/main`n",
            "refs/heads/main $head refs/heads/main $zero extra`n",
            "refs/heads/main $($head.ToUpperInvariant()) refs/heads/main $zero`n"
        )) {
        $result = Invoke-HookDirectly $repository $hookInput
        Assert-QualityCheckRan $result $repository 'unexpected hook input' 'unexpected hook input'
    }

    $unknownObject = 'e' * 40
    $result = Invoke-HookDirectly $repository "refs/heads/main $head refs/heads/main $unknownObject`n"
    Assert-QualityCheckRan $result $repository 'could not determine the pushed commits' 'unknown remote object'
}

    foreach ($key in $testRepositoryTemplates.Keys) {
        $template = $testRepositoryTemplates[$key]
        $actualRefs = @(Invoke-Git --git-dir=$($template.Remote) for-each-ref)
        Assert-True `
            -Condition ((@($templateRemoteRefs[$key]) -join "`n") -ceq (@($actualRefs) -join "`n")) `
            -Message "Template remote refs changed for '$key'."
    }

    Write-Host 'All Git hook tests passed.'
}
finally {
    Remove-Item -LiteralPath $suiteRoot -Recurse -Force -ErrorAction SilentlyContinue
}
