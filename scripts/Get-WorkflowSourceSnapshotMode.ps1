[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\A[^/\s]+/[^/\s]+\z')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('\A[1-9][0-9]*\z')][string]$RunId,
    [Parameter(Mandatory)][ValidatePattern('\A[1-9][0-9]*\z')][string]$RunAttempt,
    [Parameter(Mandatory)][string]$PreflightJobName,
    [Parameter(Mandatory)][string]$SaveStepName,
    [string]$ArtifactName = 'openclaw-source-resolution',
    [scriptblock]$ApiInvoker
)

# Decides whether this attempt captures the source ('resolve') or replays the
# run's immutable snapshot ('reuse'). The attempt number alone cannot decide:
# a fork pull request awaiting approval ends attempt 1 without running any job,
# so its approved attempt 2 has no snapshot to replay. Fresh resolution needs
# positive evidence that no earlier attempt could have uploaded the snapshot;
# anything else reuses it, and a missing artifact then fails closed.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($null -eq $ApiInvoker) {
    $ApiInvoker = {
        param($Path)
        $json = & gh api --paginate --slurp "repos/$Repository/$Path"
        if ($LASTEXITCODE -ne 0) { throw "Unable to read workflow run state from GitHub: $Path" }
        return @($json | ConvertFrom-Json)
    }
}

function Get-AttemptJobs([int]$Attempt) {
    $pages = @(& $ApiInvoker "actions/runs/$RunId/attempts/$Attempt/jobs?per_page=100")
    return @($pages | ForEach-Object { $_.jobs } | Where-Object { $null -ne $_ })
}

$attempt = [int]$RunAttempt
if ($attempt -eq 1) { return 'resolve' }

# The current attempt must list this job by name; otherwise the name passed by
# the workflow has drifted and earlier attempts cannot be read reliably.
if (@(Get-AttemptJobs $attempt | Where-Object name -CEQ $PreflightJobName).Count -eq 0) {
    throw "Attempt $attempt of run $RunId does not list the job '$PreflightJobName'; cannot determine whether a source snapshot exists."
}

$pages = @(& $ApiInvoker "actions/runs/$RunId/artifacts?name=$ArtifactName&per_page=100")
$artifacts = @($pages | ForEach-Object { $_.artifacts } | Where-Object { $null -ne $_ -and $_.name -ceq $ArtifactName })
if ($artifacts.Count -gt 0) { return 'reuse' }

for ($earlier = 1; $earlier -lt $attempt; $earlier++) {
    foreach ($job in @(Get-AttemptJobs $earlier | Where-Object name -CEQ $PreflightJobName)) {
        if ($job.conclusion -ceq 'skipped') { continue }
        if ($job.conclusion -ceq 'success') {
            # A successful capture job always leaves a snapshot behind.
            return 'reuse'
        }
        $steps = @($job.steps | Where-Object { $null -ne $_ })
        if ($steps.Count -eq 0 -and $job.status -ceq 'completed') {
            # The job ended before any step ran, so it could not have uploaded.
            continue
        }
        $save = @($steps | Where-Object name -CEQ $SaveStepName)
        if ($save.Count -eq 1 -and $save[0].status -ceq 'completed' -and $save[0].conclusion -ceq 'skipped') {
            continue
        }
        # The upload ran, or its state is unknown. Replay rather than re-resolve.
        return 'reuse'
    }
}

return 'resolve'
