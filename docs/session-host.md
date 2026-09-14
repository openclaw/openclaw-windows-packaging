# The isolated-session guest helper

`openclaw-session-host.exe` runs **inside** an MXC IsolationSession and starts
OpenClaw there. It exists for one reason: the argument vector cannot survive the
backend's execution API.

## Why a helper is required

The pinned MXC runtime accepts a single command-line **string** for execution
and flattens it through `cmd.exe`. Two corruptions were reproduced against the
real backend and are recorded in
[`mxc-compatibility-evidence.md`](mxc-compatibility-evidence.md) (gate G3):

| Input | What the guest process received |
|---|---|
| `%USERPROFILE%` | `C:\Users\<agent>` - expanded before the process saw it |
| `['q"x', 'a&b']` | `['q"x', 'a']`, exit 1, and `b` executed as a command |

Both are silent. A quote in one argument breaks quoting for a *later* one,
truncating it and running its remainder, so no character allowlist can make the
command-line form safe.

Passing the same ten hostile arguments as **data** preserved every one of them
byte for byte. Execution also does not inherit the caller's working directory -
it defaults to the system directory - so the directory must be stated
explicitly.

## Contract

The launcher writes a JSON request into the session's ephemeral workspace and
runs only the helper:

```text
openclaw-session-host.exe --request "<workspace>\<id>.json"
```

```json
{
  "schemaVersion": 1,
  "requestId": "<unique per invocation>",
  "executable": "C:\\Program Files\\nodejs\\node.exe",
  "arguments": ["C:\\...\\app\\openclaw.mjs", "%USERPROFILE%", ""],
  "workingDirectory": "E:\\repo\\some project",
  "environment": { "OPENCLAW_SUPERVISOR_MODE": "external" }
}
```

The helper validates the request, starts the executable **shell-free** with the
vector replayed exactly, applies the working directory and environment, inherits
the console streams so interactive and piped OpenClaw behave normally, and
returns the child's exit code.

`arguments` may be empty: `openclaw` with no arguments is upstream-owned
behavior and must reach the application.

The helper accepts exactly `--request <path>` and nothing else. It is reachable
from inside the session, so it must never become a general-purpose runner.

## The control result

Alongside the request the helper writes `<request>.result.json`:

```json
{ "schemaVersion": 1, "requestId": "...", "launched": true, "exitCode": 42 }
```

This is separate from the application's own output on purpose. Exit code `64`
also signals a helper failure, so without `launched` an OpenClaw process exiting
`64` would be indistinguishable from the helper never starting it.

A result may carry no `requestId` when the request itself was unreadable - that
is exactly when the control file matters most - but a result reporting
`launched: true` must always be attributable to its invocation.

`schemaVersion` mismatches are always an error. The helper and the launcher ship
in the same package, so a mismatch means a stale file or a mixed installation,
never something to interpret leniently.

## Build and packaging

The helper is a separate NativeAOT executable. It is not a packaged app, has
no app-execution alias, and takes no part in MSIX tooling.

`scripts\Build-MSIX.ps1` publishes it per architecture into
`content\session-host\<arch>\` before the packaging build, the same staged and
verified footing the MXC runtime uses, and rejects the publish if it produced
anything besides the single executable. `OpenClaw.Launcher.csproj` then carries
that directory as package content and fails the packaging build if it is absent.

The agent identity cannot execute the helper directly from another package's
`C:\Program Files\WindowsApps` directory. During `clawctl setup`, the host
copies the immutable packaged helper into a package-versioned directory under
the OS-provided shared workspace. Every foreground, gateway start, inspection,
and stop command uses that staged helper's fully qualified path. A package
update therefore requires setup to run again before execution, so the new
helper version is staged explicitly rather than silently reusing an older copy.

`scripts\Test-SigningInputs.ps1` requires the package to contain exactly one
`session-host/<arch>/openclaw-session-host.exe` matching the
`sessionHostSha256` recorded at build time. An extra file under that prefix
would be reachable from inside the session without ever having been validated.

`content\session-host\` is gitignored; the staged helper is build output, not
source. Ordinary `dotnet build` and `dotnet test` do not require it.
