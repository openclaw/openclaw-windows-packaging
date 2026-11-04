# Local Windows checks with Crabbox

Run this repository's checks in a disposable Windows Hyper-V VM without
installing the package on your host. The checked-in `.crabbox.yaml` uses native
Windows, four virtual CPUs, and 8192 MB of memory. It invokes existing
repository scripts; it does not configure cloud providers, Actions hydration,
desktop capture, signing, or package installation.

## Prepare the host and template

Use Crabbox **v0.81.0**, the version used to verify this configuration. Run
from the repository root in PowerShell 7 under your ordinary Hyper-V-authorized
account, not LocalSystem. Hyper-V, its PowerShell and storage modules, and a
usable virtual switch must already be available.

Supply a Generation 2 Windows VHDX template with a known local administrator
account. Crabbox creates a per-lease differencing disk; do not boot or change
the template itself. `transport-smoke` needs only Crabbox's Node bootstrap.
For `quality` and `tests`, qualify the template against the repository's
managed-check prerequisites: PowerShell 7, the .NET SDK selected by `global.json`,
and Node.js. Crabbox bootstraps
OpenSSH, Git, and Node/npm, not PowerShell 7, .NET, Visual Studio, or the Windows
SDK. Guest network access is needed for bootstrap and NuGet restore.

Keep the template path, switch, guest user, and password in trusted machine-local
Crabbox configuration or process environment. On Windows the normal user config
is `%APPDATA%\crabbox\config.yaml`. Supported environment names are
`CRABBOX_HYPERV_IMAGE`, `CRABBOX_HYPERV_USER`, `CRABBOX_HYPERV_SWITCH`, and
`CRABBOX_HYPERV_GUEST_PASSWORD`. Obtain the password through your local secret
workflow; do not put it in this repository, command history, or transcripts.
Set `$crabbox` to the absolute path of your verified `crabbox.exe`.

Leave `CRABBOX_CONFIG` unset for normal use. Crabbox normally merges user
configuration, `crabbox.yaml`, and `.crabbox.yaml`, then applies environment
and flags. Setting `CRABBOX_CONFIG` selects **only** that file and skips both
normal user and repository files. Selecting `.crabbox.yaml` that way loses
user-configured provider prerequisites; it does not merge them. Environment
and flag overrides still apply. Review inherited settings and overrides before
acquiring a VM; `sync.exclude` appends to inherited exclusions.

## Preview, then run

These commands do not acquire a VM:

```powershell
& $crabbox --version
& $crabbox config show
& $crabbox doctor --provider hyperv
& $crabbox sync-plan --json
& $crabbox job list
& $crabbox job run --dry-run transport-smoke
```

Confirm `hyperv`, `windows`, `normal`, the intended image/user/switch, resource
settings, and sync scope. `doctor` does not prove that the template boots or
that credentials work. Its optional-feature check may require elevation even
when the account can manage Hyper-V without elevation.

Run the smallest scenario with one PowerShell command:

```powershell
& $crabbox job run transport-smoke
```

This **transport/Node smoke** runs the existing
`plugins\gateway-isolation\index.test.js` entry through Node's test runner.
It proves native Windows job execution and that test entry, not PowerShell,
.NET, payload packaging, or the full plugin scenario suite. A nonzero Crabbox
exit code is failure; scripts invoking the command must check `$LASTEXITCODE`.
`quality` invokes the canonical restore/Release-analysis gate;
`tests` invokes the managed solution tests with restore enabled:

```powershell
& $crabbox job run quality
& $crabbox job run tests
```

Provision the required tools in your qualified template before choosing these
jobs. Missing `pwsh` or `dotnet` fails at command resolution; a missing compatible
.NET SDK fails against `global.json`. Nothing here installs them or substitutes
a passing smoke result for these checks.

These are separate checks, not a complete packaging qualification. NativeAOT,
MSIX composition, signing, deployment, and installed-upgrade lanes are not
configured. In particular, do not produce a package from this synced raw
workspace: Git seeding is disabled, so it has no source-history/provenance
contract. Use the existing [local development](local-development.md) and
[release process](release-process.md) workflows with their original verified
source snapshot, inventory, and manifest checks for package production.

## Reuse and cleanup

Each job uses `stop: auto`: a newly acquired lease is stopped after success or
failure; an explicitly reused `--id` lease remains available. Repeating a
one-shot command without `--id` creates a new VM.

For several checks against the same disposable guest, acquire once and copy the
exact lease ID printed by `warmup`. Substitute that ID below; never infer it
from a VM name or select somebody else's lease.

```powershell
& $crabbox warmup --provider hyperv --target windows --keep
# After successful acquisition, copy the returned cbx_... ID:
$leaseId = '<exact lease ID>'
try {
    & $crabbox job run --id $leaseId transport-smoke
    if ($LASTEXITCODE -ne 0) { throw 'Crabbox transport/Node smoke failed.' }
    # Edit locally, then rerun the same job with the same ID to resync.
} finally {
    & $crabbox stop --provider hyperv $leaseId
    if ($LASTEXITCODE -ne 0) { throw "Release failed; retain the ID and retry stop: $leaseId" }
}
```

Do not continue after a failed acquisition; follow its exact-lease cleanup
diagnostic. After stopping, use `crabbox status --provider hyperv --id <id>`
and read-only Hyper-V/storage inspection to verify that the owned VM and
differencing disk are gone. Do not use blanket cleanup against other VMs.

## Sync boundary

Git-mode sync uses the current worktree, including modified tracked files,
nonignored untracked files, and deletions. It does not require a push.
The config allowlists repository source, scripts, tests, plugin files,
documentation, and required root metadata. Hidden inputs such as plugin
`.setup` directories are not excluded merely for being hidden.

Built-in and repository exclusions remove `.git`, dependency/cache directories,
build output, test output, logs, common private-key/certificate files, and common
environment/credential files. Generated application payloads are not included.
No host environment variables are allowlisted for forwarding to the command.
Provider credentials are consumed on the host, not forwarded as job environment.
These patterns are not a secret scanner: inspect the actual `sync-plan` before
each new workload and keep private files outside the allowlist.

`gitSeed: false` also avoids transferring excluded content through Git history.
Windows sync sends a complete archive and `delete: true` replaces the synced
worktree on each run, so guest build outputs are not a durable cache. Save
needed evidence before releasing the lease. The 100 MiB / 10,000-file limits
fail closed if the source scope unexpectedly grows; review the plan rather
than bypassing them blindly.

Configuration and lifecycle semantics are verified against the
[v0.81.0 configuration contract](https://github.com/openclaw/crabbox/blob/v0.81.0/docs/features/configuration.md),
[Windows Hyper-V provider](https://github.com/openclaw/crabbox/blob/v0.81.0/docs/providers/hyperv.md),
[sync implementation](https://github.com/openclaw/crabbox/blob/v0.81.0/internal/cli/sync_windows_target.go),
and [named jobs](https://github.com/openclaw/crabbox/blob/v0.81.0/docs/features/jobs.md).
