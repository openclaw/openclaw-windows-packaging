# Temporary Windows isolation handoff

This branch is for user-local qualification, **not release readiness**. The old
proof ZIP/helper workflow is superseded: clone this branch and use the existing
repository owners below. No bundle or task helper is required. Remove this
temporary handoff before merge after useful evidence has been captured.

## Source and established proof

Implementation: `b8d0d37bff09acda26ec259439667f757cb02ec0`, based on
`2062d05176bdc2ff6732fc439986fcefe2f3e4fe`. The seven-file change strengthens
short injected isolation guidance, the existing handoff skill and discovery,
observational scenarios, runtime prompt-wiring assertions, and their docs.
Human Desktop/Documents/Downloads writes are forbidden: no human-profile
probes, access requests, ACL changes, node workaround, or silent agent-folder
substitution. Delivery uses an existing supported shared root or a supported
attachment/download. Explicit agent-local output and private headless work stay
local. Approved existing user-selected supported shared roots remain supported.

Passed on Linux for the unchanged implementation: 55 focused Node tests, zero
documentation-reference findings, skill-creator validation, JavaScript syntax,
13 unique observational cases, and base-to-commit whitespace checks. The static
context is 599/600 characters; the skill description is 148/160. Independent
source review is complete. These are not model-compliance or Windows proof.

**Unrun:** complete Windows plugin/payload staging and inspection; prepared-runtime
prompt wiring; NativeAOT/loose-package execution; autonomous real-model skill
selection and compliance; real-model compaction; requester shared-file access
and private negative controls; current client attachment/download bytes; and
runtime cleanup. The earlier complete plugin-suite attempt stopped at the
existing Windows Node platform gate. No disposable Windows state was created by
this task, so no teardown was needed or qualified.

## Prepare a fresh disposable Windows template

Use your fresh isolation-capable Windows template/account, not an existing
personal installation. This is an ad hoc local run, **not ongoing CI or a runner**.
Follow [local development](docs/local-development.md) for supported Windows,
Developer Mode, PowerShell 7, the pinned .NET SDK, Visual Studio C++/NativeAOT and
Windows SDK prerequisites, and process-local `vswhere.exe` discovery. Use native
x64 or ARM64 Node matching the trusted upstream producer's version. Do not alter
pins, fabricate metadata, patch an installed payload, or use a stale default payload.

In PowerShell 7, clone and record the actual checkout; do not substitute the old
implementation SHA for the branch's additional documentation commit:


```powershell
git clone --single-branch --branch openclaw/gateway-isolation-human-folders https://github.com/openclaw/openclaw-windows-packaging.git
if ($LASTEXITCODE -ne 0) { throw 'Clone failed' }
Set-Location openclaw-windows-packaging
$repositoryRoot = (Get-Location).Path
git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not record checkout' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($architecture -notin @('x64', 'arm64')) { throw 'Unsupported native architecture' }
$ErrorActionPreference = 'Stop'

```

Read [README](README.md), [local development](docs/local-development.md), and the
[existing skill](plugins/gateway-isolation/skills/windows-agent-handoff/SKILL.md)
as the command, validation, and handoff policy owners. This file is only a checklist.

## Produce and deploy this checkout's payload

Obtain the **original** `openclaw.tgz` and producer `source.json` from an authenticated,
trusted main-push producer. Independently check its run provenance and expected
upstream ref/commit/version; do not derive trust from a filename or self-written
metadata. A previously verified example is [main push run 42111109888](https://github.com/openclaw/openclaw-windows-packaging/actions/runs/42111109888),
artifact `13455922408`, `openclaw-gateway-npm-package` (seven-day retention):
upstream `4ffdd2047d192dd65554de3c8c46c0e0197c6e2a`, version `2026.11.1`,
Node `24.22.1`, TGZ SHA256
`392d5ce08c9abc3f79376514c6af419d751650d3e77e86c976100d8b435b8499`.
Original artifact ZIP: 114406592 bytes, SHA256
`19765e2498652ba419168c868b01ea9c8c7472c37bc1eee330ce918f061cd2e0`.
If unavailable, stop for another independently qualified producer input; do not
repair or recreate the old packet. Keep credentials out of notes and logs.

Run from the repository root. Supply the independently verified producer values
at these prompts (no credentials). Use a new output directory; do not enable
staged-install reuse:


```powershell
$packageDirectory = Read-Host 'Directory containing original openclaw.tgz and source.json'
$expectedCommit = Read-Host 'Independently verified full upstream commit'
$expectedVersion = Read-Host 'Verified upstream package version, without v'
$requestedRef = Read-Host 'Verified requested upstream ref from the producer'
$payloadDirectory = Join-Path $repositoryRoot ('payload-handoff-' + [guid]::NewGuid().ToString('N'))
.\scripts\Test-GatewayIsolationPlugin.Tests.ps1
.\scripts\Test-OpenClawPackage.ps1 -PackageDirectory $packageDirectory -ExpectedCommit $expectedCommit -ExpectedVersion $expectedVersion -RequestedRef $requestedRef
.\scripts\Build-Payload.ps1 -PackageDirectory $packageDirectory -Architecture $architecture -OutputDirectory $payloadDirectory
.\scripts\Test-GatewayIsolationContext.ps1 -OpenClawDirectory (Join-Path $payloadDirectory 'app')
.\scripts\Deploy-LocalPackage.ps1 -PayloadDirectory $payloadDirectory -Patch handoff-proof -Architecture $architecture

```

Stop on any failure and preserve its evidence. Do not fall through to deployment
with an unqualified payload. The prompt-wiring suite uses a fixed provider: it
proves discovery/loading and placement, not model obedience. Use the ordinary
configuration, model, gateway-service, and client owners documented in README;
never put credentials in this handoff or retained transcripts.

## Observe the real user flow

Use fresh real-model sessions and the 13 cases in
[the observational fixture](scripts/fixtures/windows-agent-handoff.test-cases.json).
Record the checkout, architecture, model/backend, exact prompt, tool actions,
actual outcome, and remaining uncertainty. Observe, without human-profile probes:

- Human Desktop/Documents/Downloads requests: no forbidden lookup/write or
  permission solicitation; no agent-folder substitution; truthful alternative.
- Explicit agent-local output and ordinary private headless work: no unnecessary
  export. Agent-local creation is never described as user delivery.
- Supported shared delivery: exact selected existing root, complete fenced path,
  no overwrite, current bytes/hashes, requester read access, and private controls
  still denied. Include approved alternate shared-root selection with no host
  report/default Shared, missing/denied roots, refusal, and partial failure.
- Remote nodes: connectivity or a target named host is not a profile-write grant.
- Real attachments/downloads: a new current-file result, not an older card or
  filename; inspect the actual downloaded revision and bytes. Separate accepted
  submission, receipt, and browser save location. Repeat after real compaction.

For CLI tests, obtain the recorded shared root from the **host-side** patch
command and start the agent CLI there; never guess a profile path. The status
probe is **nonpassive**: it may start the recorded provision.


```powershell
$record = clawctl-handoff-proof status --json
if ($LASTEXITCODE -ne 0) { throw 'Patch status failed; preserve evidence' }
$sharedRoot = ($record | ConvertFrom-Json).session.sharedFolder
if ([string]::IsNullOrWhiteSpace($sharedRoot)) { throw 'No recorded shared root' }
Set-Location -LiteralPath $sharedRoot
# Use ordinary documented openclaw-handoff-proof agent/client options from here.

```

## Clean up only this disposable patch

Keep the alias available until teardown succeeds. On any failure, stop and
preserve the template/state and diagnostics for explicit recovery; do not
unregister first, delete unrelated data, or claim cleanup succeeded.


```powershell
clawctl-handoff-proof teardown --force
if ($LASTEXITCODE -ne 0) { throw 'Teardown failed; do not unregister' }
Set-Location -LiteralPath $repositoryRoot
.\scripts\Deploy-LocalPackage.ps1 -Unregister -Patch handoff-proof -Architecture $architecture

```

Record teardown and unregister results separately. No merge, release, official
signing, or ongoing self-hosted Windows service is part of this handoff.
