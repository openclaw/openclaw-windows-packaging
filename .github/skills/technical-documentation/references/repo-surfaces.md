# Repository Documentation Surfaces and Authorities

Use only these documentation surfaces in this repository.

| Surface | Owns |
|---|---|
| `README.md` | User-facing requirements, command model, gateway startup, OpenClaw revision selection, build/test, local development, signing, installed data, and integrity boundary. |
| `CONTRIBUTING.md` | Contributor prerequisites, build/analyze/test workflow, formatting, pre-push hook, conventions, tests, PR rules, and stacked PRs. |
| `.github\copilot-instructions.md` | Canonical agent instructions. |
| `docs\clawctl-output-style.md` | Spectre.Console output contract. |
| `docs\mxc-compatibility-evidence.md` | Evidence record. |
| `.github\pull_request_template.md` | PR body structure. |
| `CONTRIBUTORS.md` | Attribution. |

## Behavior authorities

| Claim | Authority |
|---|---|
| Projects and test projects | `OpenClaw.Gateway.MSIX.slnx` |
| `clawctl` commands, help, and flags | `src\OpenClaw.Launcher\ClawCtlCommandLine.cs`; `src\OpenClaw.Launcher\Program.cs` |
| Console output shape | `src\OpenClaw.Launcher\ClawCtlConsole.cs`; `docs\clawctl-output-style.md` |
| Package identity and aliases | `src\OpenClaw.Launcher\Package.appxmanifest`; `scripts\Get-MSIXReleaseIdentity.ps1` |
| Release identity and upstream pin | `release-policy.json` |
| CI behavior, signing, and release notes | `.github\workflows\gateway-msix.yml` |
| Build and packaging steps | `scripts\*.ps1`; `scripts\LocalPackage.psm1` |
| Analyzer and format policy | `Directory.Build.props`; `.editorconfig` |
| Node and mxc runtime | `mxc-runtime.lock.json`; `scripts\Get-MxcRuntime.ps1` |

## Validation commands

Run the narrowest relevant command and state every lane not run.

```powershell
.\scripts\Test-DocReferences.ps1
.\scripts\Test-DotNetQuality.ps1
dotnet test .\OpenClaw.Gateway.MSIX.slnx --configuration Release --no-restore
git diff --check
git ls-files --eol
```

`Test-DocReferences.ps1` is an advisory checker for tracked-path existence,
relative links and anchors, and LF endings. Use `git ls-files` rather than
disk contents when validating a documentation path because untracked build
output can be stale.
