---
name: deslop
description: >-
  Perform a diff-scoped, behavior-neutral cleanup of AI-generated slop on the
  current branch before code review. Use for removing trivial comment,
  defensive-check, type-laundering, redundant-intermediate, compatibility-shim,
  or local style drift from the branch diff. Do not use for full correctness or
  safety review, documentation work handled by technical-documentation or
  docs-refactor, or repo-wide refactors.
---

# Deslop

Clean only the current branch diff before review. Preserve behavior absolutely:
this is a narrow pre-review polish pass, never a correctness, safety, or
documentation review.

## Operating contract

| Concern | Rule |
|---|---|
| Use when | The current branch needs a diff-scoped cleanup of trivial AI-generated slop before code review. |
| Do not use when | The request is a full correctness or safety review, documentation work (`technical-documentation` or `docs-refactor`), or a repo-wide refactor. |
| Required inputs | A clean understanding of the current branch diff, its merge base with `origin/main`, and the surrounding code and tests for every changed hunk. |
| Mutation boundary | Make behavior-neutral edits within the branch diff only; no functional change, no repo-wide sweep, and no analyzer-policy change. |
| Done when | Re-inspect the diff, `.\scripts\Test-DotNetQuality.ps1` is green, and report what changed and what was left for the author. |

## Workflow

1. Refresh and establish the exact review range before inspecting code:

   ```powershell
   git fetch origin main
   git merge-base origin/main HEAD
   git diff origin/main...HEAD
   ```

   Use `origin/main...HEAD` when the merge base is `origin/main`; otherwise
   inspect the merge-base-to-`HEAD` diff. Never turn this into a repository-wide
   search, lint cleanup, or formatting pass.

2. Read every changed hunk with enough surrounding code to determine its local
   convention and observable contract. Inspect additions for the slop categories
   below. Let existing analyzer diagnostics stand; this pass finds what analyzers
   cannot prove.

3. Classify a finding before editing. Remove it inline only if the edit is
   trivial and demonstrably behavior-neutral. Preserve API shapes, exceptions,
   execution order, resource lifetime, console output, process arguments,
   serialization, and PowerShell failure behavior. If any of those could
   change, leave the code intact and name the concern for the author.

4. For a C# cleanup, preserve the repository analyzer policy. `Directory.Build.props`
   enables `AnalysisMode=All`, build-time code-style analysis, and
   `TreatWarningsAsErrors`; rule severities belong in `.editorconfig`. Never
   weaken a rule repository-wide to clear one site. A necessary narrow
   suppression needs a written rationale beside the code.

5. For `clawctl` output, retain the renderable composition contract in
   `src\OpenClaw.Launcher\ClawCtlConsole.cs`: use Spectre.Console renderables
   such as `Grid`, `Panel`, `Paragraph`, and `Rows`, with `Paragraph.Append`
   for styled text. Do not replace that with interpolated markup strings; it
   changes escaping and rendering behavior described in
   `docs\clawctl-output-style.md`.

6. For tests in the diff, retain observable assertions. Do not add or preserve
   tests that read source files for implementation-string markers. Keep tests
   isolated from real user state and deterministic: no sleeps, hardcoded ports,
   or inter-test ordering dependencies.

7. Do not treat launcher argument handling as cleanup territory. `openclaw`
   arguments are OpenClaw-owned: do not add host-only switches, consume `--`, or
   rewrite the argument vector.

8. Preserve LF text. Do not use whole-file `Set-Content` or `Out-File` rewrites;
   `.gitattributes` and `.editorconfig` require tracked text to stay LF.

9. Verify only after every edit is complete:

   ```powershell
   git diff --check
   .\scripts\Test-DotNetQuality.ps1
   dotnet test .\OpenClaw.Gateway.MSIX.slnx --configuration Release --no-restore
   git ls-files --eol
   ```

   Re-read the final diff against the same merge base. Do not claim a cleanup
   was safe merely because it compiles.

10. Report in a few sentences: state whether anything changed, name each
    non-trivial item left for the author, and say that code review remains the
    required correctness and safety gate.

## What to remove, and what to preserve

| Real slop in the branch diff | Required pattern that can look like slop |
|---|---|
| Narrating comments, syntax explanations, or prose that repeats code without explaining a non-obvious reason. | Policy comments that explain *why*, including the substantial policy rationale in `scripts\Get-PackagingRelevance.ps1` and `.github\workflows\gateway-msix.yml`. |
| Null guards, `try`/`catch`, or existence checks protecting only imagined states that are abnormal for the surrounding type. | `$ErrorActionPreference = 'Stop'` and explicit `$LASTEXITCODE` checks after native tools in PowerShell build scripts; these are intentional fail-fast behavior. |
| Gratuitous `object` round-trips, unnecessary casts, `dynamic`, or reflection. | Contract-specific source-generated `JsonSerializerContext` metadata; reflection is a NativeAOT defect, not a stylistic shortcut. |
| One-use variables or helpers that add no domain meaning, remove no duplication, and simplify no control flow. | Hash and metadata validation in packaging scripts; this is a release trust boundary and must never be simplified away. |
| Aliases, retries, or fallback branches with no named shipped contract or removal plan. | Spectre.Console renderable composition (`Grid`, `Panel`, `Paragraph`, `Rows`, and `Paragraph.Append`) rather than interpolated markup strings. |
| Naming, `using` placement, control flow, or formatting that visibly conflicts with neighboring code. | Local conventions intentionally established by surrounding code, even if a generic cleanup preference differs. |

## Guardrails

- Run this pass before code review, never instead of it. Review remains the
  required correctness and safety gate.
- Do not remove validation, error reporting, cleanup, compatibility behavior,
  or comments whose rationale is not fully understood.
- When an apparent cleanup intersects NativeAOT, packaging trust boundaries,
  PowerShell native-command handling, output rendering, tests, or argument
  forwarding, leave it for the author unless equivalence is obvious and proven.
