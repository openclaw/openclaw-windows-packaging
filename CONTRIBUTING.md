# Contributing

This repository builds the `OpenClaw.Gateway` MSIX package and the
`openclaw.exe` NativeAOT launcher it ships. It is Windows-focused and pins the
.NET SDK through `global.json`. Run every command below from the repository
root in PowerShell 7 (`pwsh`).

The contributor guides cover the workflows and architecture behind the command
summary below:

- [Architecture](docs/architecture.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Local development](docs/local-development.md)
- [Official release process](docs/release-process.md)

## Prerequisites

- PowerShell 7
- The .NET SDK feature band pinned in `global.json`
- Visual Studio Build Tools with the **Desktop development with C++** workload
  and the Windows SDK, for NativeAOT publish and MSIX composition

## Build, analyze, and test

```powershell
dotnet restore .\OpenClaw.Gateway.MSIX.slnx

# Canonical quality gate: restore plus a Release rebuild with static analysis.
.\scripts\Test-DotNetQuality.ps1

dotnet test .\OpenClaw.Gateway.MSIX.slnx --configuration Release --no-restore
```

`Test-DotNetQuality.ps1` is the single entry point used by local development,
continuous integration, and the optional pre-push hook, so all three report the
same result. It rebuilds with static analysis and then verifies whitespace and
code style. It always rebuilds: an up-to-date project is skipped and reports no
analyzer diagnostics at all. It never rewrites source.

Run one test by fully qualified name:

```powershell
dotnet test .\tests\OpenClaw.Launcher.Tests\OpenClaw.Launcher.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName=OpenClaw.Launcher.Tests.HostOptionsTests.ParseForwardsAllArgumentsUnchanged"
```

Run the PowerShell policy suites when you change the workflow, signing inputs,
or package version logic:

```powershell
.\scripts\Test-SigningInputs.Tests.ps1
.\scripts\Test-NodeRuntimeInputs.Tests.ps1
.\scripts\Test-PackagingRelevance.Tests.ps1
.\scripts\Test-OpenClawCacheKey.Tests.ps1
.\scripts\Test-OpenClawPackage.Tests.ps1
.\scripts\Test-MSIXReleaseIdentity.Tests.ps1
.\scripts\Test-WorkflowPackageVersion.Tests.ps1
.\scripts\Test-GitHooks.Tests.ps1
```

The Node.js input suite requires Node.js and npm. It builds a dependency-free
local fixture; it does not download or build OpenClaw.

Run the NativeAOT publish when you change host JSON, reflection, interop, or
anything else that is trimming-sensitive. A JIT `dotnet build` does not
exercise that path:

```powershell
$vsInstaller = Join-Path `
  ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) `
  'Microsoft Visual Studio\Installer'
$env:Path = "$vsInstaller;$env:Path"
dotnet publish .\src\OpenClaw.Launcher\OpenClaw.Launcher.csproj `
  --configuration Release --runtime win-x64 --self-contained
```

Run the native `clawctl` gate when you change command-line parsing, help,
version output, or the host startup path. The xUnit suite runs under a JIT test
host, so it cannot see the entrypoint alias or root command name that the
launcher derives from native `argv[0]`, and a successful publish is not
execution evidence. The script publishes the scenario driver in
`tests\OpenClaw.Launcher.AotSmoke` for win-x64 with NativeAOT into a temporary
directory it owns, runs it as `clawctl.exe`, repeats the run under a wrong
executable name to prove the alias check is real, and removes the directory
afterwards:

```powershell
.\scripts\Test-NativeAotCli.Tests.ps1
```

The driver calls the same `Program.RunAsync` that the shipped `Main` calls, so
startup diagnostics, argument routing, the error boundary, and disposal are all
covered. It must never call `Main` itself: `Main` resolves the diagnostic log
under the user's profile, so a gate built on it would append to your real
`%LOCALAPPDATA%\OpenClawGatewayMSIX` log. Add scenarios by injecting
fixture-owned collaborators through `HostStartup` — an explicit temporary
diagnostic path, in-memory writers, and Node/launch delegates that cannot start
a real process.

## Formatting and static analysis

Formatting and analyzer severity are defined by the root `.editorconfig`. The
analyzer properties themselves live in `Directory.Build.props`, and the
repository uses only the analyzers that ship with the pinned SDK.

Continuous integration never rewrites source. To apply formatting locally,
review the resulting diff before committing:

```powershell
dotnet format whitespace .\OpenClaw.Gateway.MSIX.slnx
dotnet format style .\OpenClaw.Gateway.MSIX.slnx
```

To check without writing files:

```powershell
dotnet format whitespace .\OpenClaw.Gateway.MSIX.slnx --verify-no-changes
dotnet format style .\OpenClaw.Gateway.MSIX.slnx --verify-no-changes
```

`--verify-no-changes` exits with code 2 when it finds violations.

The build is warning-free and `TreatWarningsAsErrors` is on, so any new warning
fails the build. That includes compiler diagnostics and analyzers this
repository has never seen, such as those introduced by an SDK upgrade. When one
appears, fix it or add a narrow suppression with a written rationale next to
the code it applies to. Do not commit a generated suppression baseline, and do
not relax a rule repository-wide to get past a single site.

NuGet audit advisories (`NU1901`-`NU1904`) are deliberately excluded from the
error gate. A newly published advisory can appear against an unchanged
dependency graph, and breaking `main` with no committed change and no in-build
fix helps nobody. They stay visible as warnings and are triaged as security
work.

`.gitattributes` normalizes tracked text to LF and `.editorconfig` sets
`end_of_line = lf`. Avoid whole-file rewrites through `Set-Content` or
`Out-File`, which can reintroduce CRLF. Verify with `git ls-files --eol`.

A checkout always produces LF, so this only bites on files you have just
created. Many Windows editors write CRLF by default, so a brand-new `.cs` file
can fail the whitespace check before its first commit with
`error ENDOFLINE: Fix end of line marker`. Run
`dotnet format whitespace .\OpenClaw.Gateway.MSIX.slnx` to normalize it, or
configure your editor to write LF for this repository.

## Optional pre-push hook

You can have the quality gate run before every push instead of finding out from
CI. The hook is opt in and local to your clone:

```powershell
.\scripts\Install-GitHooks.ps1
```

That writes the tracked `hooks\pre-push` into the hooks directory Git consults
for your working tree. It runs `Test-DotNetQuality.ps1` and nothing else, so it
reports exactly what CI reports. To remove it:

```powershell
.\scripts\Install-GitHooks.ps1 -Remove
```

A clone has one hooks directory, shared by every linked worktree
(`git worktree add`). Installing or removing from any worktree therefore
affects all of them, and each push runs the quality script from the worktree
you pushed. Other clones are unaffected.

Both operations are idempotent. Installation refuses to overwrite a `pre-push`
hook it did not write, removal only deletes a hook carrying its own marker, and
neither touches your global Git configuration or `core.hooksPath`.

Skip the hook for a single push with `git push --no-verify`. The hook is a
latency shortcut, not a policy boundary: it lives in one clone, it is
bypassable, and required CI checks remain authoritative.

## Repository conventions

- Ordinary builds and tests must leave `IncludePackagingContent` unset.
  Packaging builds set it to `true` and supply a runtime identifier.
- Treat launcher arguments as OpenClaw-owned. Do not add host-only switches,
  consume `--`, rewrite arguments, or block upstream commands. The
  System.CommandLine tree covers `clawctl` only; the `openclaw` entrypoint must
  keep forwarding its argument vector without parsing it.
- Preserve execution of the packaged `app\openclaw.mjs` from the read-only MSIX
  package, but run it inside the isolated session rather than launching it
  directly. `clawctl setup` owns session provisioning and writes the setup
  marker that `openclaw` requires. Node.js is installed into the agent
  account's profile by the session host, not into package LocalState; do not
  copy the OpenClaw application payload or use device-installed Node.js.
- Keep x64 and ARM64 behavior synchronized across the workflow matrix, scripts,
  project runtime identifiers, manifest content, and signing validation.
- Metadata files are part of the release trust chain. Coordinate changes across
  payload creation, MSIX creation, signing validation, workflow artifacts, and
  tests.
- Keep official release identity derived from `gatewayTag` and `msixRevision`.
  The unsuffixed Gateway tag owns revision block `1000-1999`; correction tags
  `-2` through `-64` own their corresponding 1,000-number blocks. Use revision
  `0` for the first MSIX of a Gateway tag and increment only for packaging-only
  rebuilds of that exact tag. Do not assign package versions or release tags by
  hand.
- Treat published proof releases in `scripts/msix-upgrade-baselines.json` as
  immutable transition fixtures. Keep their release asset names and SHA-256
  digests pinned. Version-policy changes must pass the installed-package
  upgrade job from every standalone and bundle baseline, retain package
  LocalState, and prove both candidate delivery formats install fresh. Run the
  harness only on an isolated clean Windows account; it refuses pre-existing
  OpenClaw Gateway registrations and cleans up only its own installation.
- Use source-generated `System.Text.Json` metadata through `OpenClawJsonContext`.
  The launcher is NativeAOT and must not introduce reflection-based
  serialization.
- PowerShell scripts set `$ErrorActionPreference = 'Stop'` and must also check
  `$LASTEXITCODE` after invoking native tools.
- Package versions have four numeric components that each fit in `UInt16`.
  Package dependency versions belong in `Directory.Packages.props`.

## Tests

- A test earns its place by the realistic defect it would catch. Prefer
  functional and integration tests that drive the real path.
- Assert observable behavior: return values, emitted events, persisted state,
  exit codes, rendered output. Do not read a source file and assert on string
  markers of the implementation.
- Tests must never modify real user state. Use the isolated temporary
  directory fixtures rather than touching a real OpenClaw profile, packaged
  LocalState, or an installed MSIX.
- Tests must be deterministic: no sleep-based synchronization, hardcoded ports,
  or inter-test ordering dependencies.

## Documentation

`README.md`, `CONTRIBUTING.md`, and `.github\copilot-instructions.md` are a
three-way contract. They must agree with each other and with the source that
owns each behavior claim. When they disagree, source wins — verify the claim
and correct the documents rather than picking whichever reads best.

Check documentation references before you push:

```powershell
.\scripts\Test-DocReferences.ps1
```

It validates that repository paths named in markdown are actually tracked,
that relative links and their anchors resolve, and that markdown stays LF. It
resolves paths through `git ls-files` rather than the filesystem, because a
stale untracked build directory can make a renamed project path look valid.
Findings exit nonzero locally. Continuous integration runs the same script with
`-Advisory`, which annotates the pull request without failing the build, so a
documentation change is never blocked by it.

Three project skills in `.github\skills\` support this work:

| Skill | Use it for |
|---|---|
| `technical-documentation` | Writing, reviewing, or auditing any documentation surface |
| `docs-refactor` | Rewriting, splitting, or reorganizing a page without losing behavior facts |
| `deslop` | Behavior-neutral cleanup of a branch diff before code review |

## Pull requests

Use the pull request template. Title the PR
`type: user-facing description`, where `type` is one of `feat`, `fix`,
`improve`, `refactor`, `docs`, or `chore`. Describe the outcome rather than the
mechanism.

Lead with the problem and user or contributor impact in short, plain-language
sentences, followed by a brief explanation and useful evidence. Keep technical
details optional, but important risks and required actions visible. Name the exact
head SHA your evidence came from and state which validation lanes you did not
run.

Keep the description current when review feedback changes the implementation;
the body is the durable explanation, not just the comment thread. Keep **Allow
edits from maintainers** enabled so a maintainer can update the branch.

This repository keeps no changelog file. An official release calls
`softprops/action-gh-release` with `generate_release_notes: true`, so GitHub
composes the release notes from the titles of the pull requests merged since
the previous release tag. Your pull request title is the release note, which is
why the `type: user-facing description` form matters.

### Stacked pull requests

Larger work may land as a linear stack of dependent PRs, each with a single
review boundary. When a change belongs to a stack:

- State `Layer N of M`, the immediate parent, and the ordered stack in the PR
  body.
- Validate each layer against its immediate parent, not only against the top of
  the stack.
- Merge bottom-up, and retarget the child PR to the parent's base before the
  parent merges.
