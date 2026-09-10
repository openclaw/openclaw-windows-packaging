# Contributing

This repository builds the `OpenClaw.Gateway` MSIX package and the
`openclaw.exe` NativeAOT launcher it ships. It is Windows-focused and pins the
.NET SDK through `global.json`. Run every command below from the repository
root in PowerShell 7 (`pwsh`).

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
.\scripts\Test-WorkflowPackageVersion.Tests.ps1
```

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

The analysis policy is being rolled out in stages. Rules currently reported as
warnings are a known backlog rather than an invitation to add new violations;
each is being cleared and promoted to `error` one family at a time. Do not
commit a generated suppression baseline. Fix the diagnostic, or add a narrow
suppression with a written rationale.

`.gitattributes` normalizes tracked text to LF and `.editorconfig` sets
`end_of_line = lf`. Avoid whole-file rewrites through `Set-Content` or
`Out-File`, which can reintroduce CRLF. Verify with `git ls-files --eol`.

A checkout always produces LF, so this only bites on files you have just
created. Many Windows editors write CRLF by default, so a brand-new `.cs` file
can fail the whitespace check before its first commit with
`error ENDOFLINE: Fix end of line marker`. Run
`dotnet format whitespace .\OpenClaw.Gateway.MSIX.slnx` to normalize it, or
configure your editor to write LF for this repository.

## Repository conventions

- Ordinary builds and tests must leave `IncludePackagingContent` unset.
  Packaging builds set it to `true` and supply a runtime identifier.
- Treat launcher arguments as OpenClaw-owned. Do not add host-only switches,
  consume `--`, rewrite arguments, or block upstream commands.
- Keep x64 and ARM64 behavior synchronized across the workflow matrix, scripts,
  project runtime identifiers, manifest content, and signing validation.
- Metadata files are part of the release trust chain. Coordinate changes across
  payload creation, MSIX creation, signing validation, workflow artifacts, and
  tests.
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

## Pull requests

Use the pull request template. Title the PR
`type: user-facing description`, where `type` is one of `feat`, `fix`,
`improve`, `refactor`, `docs`, or `chore`. Describe the outcome rather than the
mechanism.

Every PR body should explain the problem it solves, why the change was made,
the user or contributor impact, and the evidence that it works. Name the exact
head SHA your evidence came from and state which validation lanes you did not
run.

Keep the description current when review feedback changes the implementation;
the body is the durable explanation, not just the comment thread. Keep **Allow
edits from maintainers** enabled so a maintainer can update the branch. Do not
edit `CHANGELOG.md`.

### Stacked pull requests

Larger work may land as a linear stack of dependent PRs, each with a single
review boundary. When a change belongs to a stack:

- State `Layer N of M`, the immediate parent, and the ordered stack in the PR
  body.
- Validate each layer against its immediate parent, not only against the top of
  the stack.
- Merge bottom-up, and retarget the child PR to the parent's base before the
  parent merges.
