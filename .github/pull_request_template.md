<!--
Optional linked context:
Add a visible `Closes #<issue-number>` or `Related: #<issue-number>` line
below this comment.

Required PR title:
type: user-facing description
Use a parenthesized scope only when it adds clarity:
fix(launcher): report an actionable error when Node.js is missing

Types: feat, fix, improve, refactor, docs, chore.
For fixes, describe the user-visible symptom and trigger:
fix: launcher exits without a diagnostic when Node is missing
Avoid implementation details such as:
fix: add null check to payload resolver

**MUST:** Keep **Allow edits from maintainers** enabled for this PR so
maintainers can help update the branch when needed.

See [CONTRIBUTING.md](../CONTRIBUTING.md) for the validation commands this
repository expects and for the packaging, signing, and NativeAOT rules that
apply to launcher changes.
-->

## What Problem This Solves

<!--
Describe the concrete user, contributor, security, or operational problem.
Use one short, plain-language sentence. For fixes, prefer:
"Fixes: <what goes wrong> when <trigger or condition>."
For other changes, describe the need without inventing a bug.

Name the affected surface: the `openclaw.exe` launcher, packaged application,
MSIX composition, the signing policy, or the GitHub Actions workflow. Do not
describe the code-level cause here.
-->

## User Impact

<!--
"User impact: <what users, operators, or contributors can now do or expect>."
Lead with the concrete outcome in plain language, usually one sentence.
For internal-only changes, say there is no user-visible change; do not invent a benefit.
Keep important risks, breaking changes, migrations, and required user actions visible here.
For tooling or policy changes, describe any actual contributor or reliability
benefit without claiming a user-visible behavior change.
-->

## Why This Change Was Made

<!--
Briefly explain how the change addresses the problem without repeating the impact.
Keep the body short. Leave file lists, internal acronyms, and root-cause walkthroughs
in the diff or optional <details>; include technical detail only when it explains
behavior or a material tradeoff. Do not hide risks or required actions in <details>.
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
Summarize what was checked and the result; note meaningful gaps. Link long output
or put it in optional <details>, keeping the useful evidence summary visible.
-->
