<!--
Optional linked context:
Add a visible `Closes #<issue-number>` or `Related: #<issue-number>` line
below this comment.

Required PR title:
type: user-facing description
Use a parenthesized scope only when it adds clarity:
fix(staging): launcher fails to start when the prepared payload is partial

Types: feat, fix, improve, refactor, docs, chore.
For fixes, describe the user-visible symptom and trigger:
fix: launcher exits without a diagnostic when Node is missing
Avoid implementation details such as:
fix: add null check to payload resolver
-->

<details>
<summary>Additional instructions</summary>

**MUST:** Keep **Allow edits from maintainers** enabled for this PR so
maintainers can help update the branch when needed.

See [CONTRIBUTING.md](../CONTRIBUTING.md) for the validation commands this
repository expects and for the packaging, signing, and NativeAOT rules that
apply to launcher changes.

</details>

## What Problem This Solves

<!--
Describe the concrete user, contributor, security, or operational problem.
For fixes, begin with:
"Fixes an issue where users <do X> would <experience Y> when <condition>."

Name the affected surface: the `openclaw.exe` launcher, payload staging, the
MSIX package, the signing policy, or the GitHub Actions workflow. Do not
describe the code-level cause here.
-->

## Why This Change Was Made

<!--
In one or two sentences, explain the complete shipped solution, key design
decisions, and relevant boundaries or non-goals. Include implementation detail
only when it helps reviewers understand user-visible behavior or risk.
Avoid file-by-file narration.
-->

## User Impact

<!--
State what users, operators, or contributors can now do or expect. Lead with
the concrete benefit. If there is no user-visible behavior change - which is
normal for tooling and policy changes - say so plainly and name the
contributor or reliability benefit instead.
-->

## Evidence

<!--
Show the most useful proof that this change works, and name the exact head SHA
the evidence came from.

Record the commands you ran and their outcome, for example:
- .\scripts\Test-DotNetQuality.ps1
- dotnet test .\OpenClaw.Gateway.MSIX.slnx --configuration Release
- .\scripts\Test-SigningInputs.Tests.ps1
- dotnet publish ... --runtime win-x64 --self-contained (NativeAOT)

Include CI links, artifact links, redacted logs, or terminal output. State
which lanes you did not run and why. Reviewers will inspect the code, tests,
and CI; use this section to make validation easy to understand rather than to
restate the diff.
-->
