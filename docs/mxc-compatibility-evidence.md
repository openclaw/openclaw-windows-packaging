# MXC session runtime and operational evidence

This document describes the packaged isolated-session implementation and the
observable guarantees it makes. It does not describe an external MXC protocol
as a stable public API.

## Runtime boundary and MXC seam

The launcher owns a small session contract in
`OpenClaw.Launcher\Mxc\IMxcSessionClient`. `MxcSdkSessionClient` is the current
implementation: it calls the official `Microsoft.Mxc.Sdk` 1.0.0 V1 lifecycle
and translates typed results and errors. The SDK owns MXC serialization and
containment; the launcher still owns package identity, recorded sessions,
recovery, and its guest-helper protocol.

NuGet supplies the complete Windows native unit beside the published
launcher: `mxc_ffi.dll`, `plm.exe`, `wslcsdk.dll`, and
`wxc-wslc-daemon.exe`. The integrity manifest and verifier check the restored
archive and selected native bytes before either local or MSIX composition.
The runtime locator binds SDK imports to the exact module, restricts dependency
search to system libraries and clears `MXC_FFI_DIR` before native calls, without
honoring an executor-directory override. Shipped bytes are checked at
build/release time; development layouts remain user-controlled. There is no
per-launch native content hash or reparse-point refusal.

Interactive execution uses the public SDK PTY API and a host console bridge;
redirected or mixed streams use separate pipes. Every relay belongs to that
invocation and is joined before disposal. The SDK's asynchronous cancellation
alone is not a termination guarantee: the adapter requests SDK kill-and-wait, while
dispatched provision/lifecycle calls finish so their outcome is not lost.

The tagged IsolationSession termination primitive reaches the foreground
process, not a confirmed invocation tree. The helper's Node.js or PowerShell
child and backgrounded descendants are not proven stopped by SDK `Kill()`.
Fixture cancellation flags and a cooperative Ctrl+C exit do not establish that
stronger guarantee. The branch-local invocation-job prototype below supplies
additional lifecycle control; SDK cancellation alone remains insufficient.
Stopping the persistent session is not a substitute because it would disrupt
unrelated work.

### Branch-local descendant-cancellation prototype

On Windows 26713 with SDK/native runtime 1.0.0, a medium-integrity unpackaged
host provisioned a disposable IsolationSession without an application ID.
A standalone x64 NativeAOT helper ran synthetic PowerShell trees under the
minted agent account in a separate Windows session. No package was registered.
The legacy `--request` baseline reproduced the defect: SDK `Kill()` followed
by `WaitAsync()` returned while the helper, workload, child, and grandchild
remained alive. Releasing the fixture removed those baseline processes.

The prototype's host-owned named job accepted the agent's assignment-only
handle across that actual token/session boundary. Initial attached/captured
cancellation and normal-exit observations found no enumerated survivors or
late sentinel, with an unrelated workload in the same agent session surviving.
An expanded run then hit `AccessDenied` when the medium host tried to inspect
guest process exit. That run is inconclusive, not passed. Both runs performed
exact-ID stop and deprovision; before/after local-account and `Win32_UserProfile`
snapshots independently verified removal of their minted accounts, profiles,
temporary directories, and shared workspaces.

The stronger observation fixture grants only query-limited-information and
synchronization access to the host SID on its synthetic process objects,
including its helper, and pins those exact handles before cancellation.
It changes no groups, policy, token privileges, or real-profile objects;
`AccessDenied` never counts as exit. The local injected-deadline regression
then failed: the helper handle was signaled, but three workload/child/descendant
handles were not signaled when execution returned, despite zero job members.
This exposed an exit-drain gap rather than complete cancellation proof.
It was not hidden with sleeps, retries, or weaker assertions. A subsequent
local run confirmed all four processes belonged to their exact invocation job
before cancellation and that the three failed waits returned `WAIT_TIMEOUT`,
not `WAIT_FAILED`.

A bounded local admission probe then lowered that job's active-process limit
to one while its four existing members remained alive. A synchronized new
child creation failed with Win32 error 1816 and produced no sentinel, while
the original members and unrelated sibling survived.

The updated candidate keeps a query-only guest coordinator outside the job.
After the host closes admission, it pins the workload members under the agent
identity, asks the host to terminate, and waits those exact handles before
acknowledging drain. Targeted local tests now pass strict exit checks for the
worker helper, workload, child, descendant, and outside coordinator, plus
sibling survival. An injected native-spawn delay proves cancellation cannot
start late work. Deterministic blocked-reader tests prove failed drain is
reported after both readers are interrupted and joined.

A fresh Windows 26713.1000 run used the standalone x64 NativeAOT helper with
SDK/native runtime 1.0.0 in one disposable IsolationSession. It disabled all
host-observer process DACL grants. An independent same-agent witness pinned
the helper, workload, child, descendant, and outside coordinator for each of
two concurrent invocations before cancellation. Across attached cancellation,
captured cancellation, normal completion, an injected deadline, and a
completion/cancellation race, the first invocation's five handles were signaled
at return while the sibling's five remained running. Releasing the sibling
then signaled all five. All five scenarios passed without a late sentinel;
their 15 raw matrices contain 150 wait observations and no native wait errors.
Exact-ID stop and deprovision completed, and independent account/profile/path
snapshots verified the minted account, profile, temporary directories, and
workspace were gone while pre-existing accounts/profiles remained.

The regression suite also holds an extra job handle through termination,
rejects cancelled-before-admission launches, and races normal completion
against cancellation. It uses real helper/workload processes and never
provisions an account.
The separately invoked `CancellationPrototypeProbe.RunDisposableAsync` test
probe requires explicit disposable-session authorization and independent
before/after account/profile snapshots; it is not an automatic test lane.

This remains a branch-local ordinary-leak prototype, not a hostile-agent
security boundary or a shipping release. The coordinator's managed quality
gate passed without warnings or errors, and all 1,318 Release tests passed.
The full x64 NativeAOT driver passed 33 scenarios and its expected wrong-alias
negative. Real keyboard/PTY
cancellation, abrupt host death with a retained handle, installed-package
execution, and ARM64 execution are not established by this run.

An additional deterministic natural-exit fixture closes admission, takes a real
exact-job snapshot, then proves a member's natural exit with an independent
signaled handle before closing the creator/test references and reopening once.
The OS returned a valid, same-identity, same-job, signaled handle; real error
87 was not observed. The expiration branch is covered separately through an
injected native-open seam using that real positive snapshot PID and the fixed
`PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE` mask.

The coordinator accepts only error 87 in that narrowly controlled context,
following [.NET 10's matching nonzero-PID handling](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs#L34-L50)
and the documented [PID lifetime](https://learn.microsoft.com/en-us/windows/win32/procthread/process-handles-and-identifiers).
Synthetic access-denied, invalid-handle, other-error, reused/nonmember, and
invalid-PID cases remain failures. This is synthetic failure-path coverage,
not a claim that the OS expiration error occurred in the real scenarios.
Every retained process handle still requires signaled exit; the strict
ten-handle scenarios are unchanged. No inverse NTSTATUS inference or Native
System Services calls are used. Full PID-reuse stress, hostile same-identity
manipulation, and the unrun deployment/interactive lanes remain outside the
observed-flow proof; no universal shipping guarantee is claimed.

After that narrow classification change, the new source again passed the
managed quality gate, all 1,318 Release tests, the standalone x64 NativeAOT
publish, and the full 33-scenario native driver with its expected negative.
A separate fresh natural-rights IsolationSession repeated all five flows with
the unchanged strict witness: 15 matrices, 150 process-handle waits, no errors,
no late effects, and sibling survival. Independent exact-ID account/profile/
temporary-directory/workspace cleanup verification passed. This repeats the
ordinary-flow proof on the changed source; it does not turn injected error-87
coverage into an observed native expiration.

The earlier locally packed SDK spike's attached-console API is no longer
public in V1. Its historical live results do not qualify this SDK version.
The upstream IsolationSession EOF test remains marked as blocked on a
platform-held stdin write handle. Closing the SDK writer is attempted, but
does not establish that an EOF-reading workload will finish on a given OS.
Real keyboard/Ctrl+C/resize, piped EOF, and legacy-provision upgrade proof
require a separately authorized disposable Windows environment.

### Local SDK 1.0 qualification

A side-by-side x64 Developer Mode patch was exercised on Windows
26703.1002. Setup, recorded-session reuse and explicit stale-session recovery,
packaged OpenClaw execution, PowerShell command/file execution, separate
streams, Unicode arguments and keyboard input, initial PTY size, Ctrl+C exit
130, and host-and-session collection passed. A synthetic loopback gateway on
an allocated port served HTTP health requests and survived helper disposal and
foreground interruption; start, status, restart, stop, and teardown passed.
The patch, its accounts/profiles, recovery task, and listener were removed,
and the existing base registration was unchanged.

This run found and fixed input-reader shutdown cancellation, helper quoting
after the SDK's PTY startup gate, and guest console encoding. The guest helper
now initializes attached children with UTF-8 rather than inheriting the
isolated console's OEM code page.

Piped input arrived, but closing host stdin did not deliver EOF to the reading
guest; the bounded scenario timed out. The upstream/platform EOF limitation
remains unqualified. This loose-layout run did not prove real resize events,
installed MSIX/native redirection, legacy-runtime session replay, installed
upgrades, or ARM64 execution.

`OpenClaw.SessionProtocol` is separate from that backend seam. It is the
versioned, launcher-to-guest request/result contract used for execution,
inspection, runtime installation, and collection. A backend execution request uses a controlled inner `cmd.exe /d /s /c`
command line only to start the staged helper. Its explicit quoting works both
under the SDK's pipe dispatch and after its PTY startup gate; the requested
executable and argument vector remain JSON data rather than interpolated
into that command line.

## Session ownership and routing

`clawctl setup` is the explicit lifecycle entry point. It writes package-local
setup state and records the session that this installation owns in
`session.json`. Ownership is never inferred from a machine account or profile:
unrelated agent accounts may exist, and teardown must remain safe and
idempotent. When MXC reports that this recorded provision is missing, setup
reprovisions a replacement for this installation and removes only a gateway
record that names that explicitly stale session; it neither adopts unrelated
machine agents nor disturbs a gateway record for any other session.
`clawctl teardown --force` explicitly confirms deletion of the recorded owned
session, its data, and local setup state; it does not uninstall the package.

`clawctl setup --fresh` explicitly authorizes a reset of this installation.
It captures a redacted pre-reset report, performs the normal owned teardown,
then clears only the contents of the package-owned state roots before running
the ordinary setup route. It refuses unpackaged execution, never follows
reparse points, and stops without a wipe or replacement setup when teardown is
incomplete. It does not onboard OpenClaw or start the gateway.

`openclaw` always runs inside the isolated session recorded for this
installation. When its setup marker is absent, it provisions that session
before launching; the lifecycle lock double-checks the marker so concurrent
first launches provision only once. It leaves unreadable, incomplete, foreign,
newer-schema, preparing, and tearing-down markers, session mismatches, and
stale agent Node.js runtimes for their explicit `clawctl setup` or
`clawctl teardown` recovery paths. There is no environment variable, option,
or automatic fallback that runs OpenClaw on the host. Before touching any
recorded state, both `clawctl setup` and `openclaw` require that this machine
can host a session: an MXC runtime
that is unavailable, a backend that reports no isolation-session support, a
host build measured as unsupported, or a missing package identity all fail with
the same message, which names Windows Update or a newer Windows version as the
remedy and prints the diagnostic log path. Support that cannot be determined
(the host build is unreadable and the backend probe failed) is not a refusal:
provisioning is attempted so the backend's own error surfaces.
`clawctl status` preserves the recorded ownership record and does not provision
or replace a session. It is not passive: `ProbeRecordedStatusAsync` invokes the
backend `StartAsync` operation for the recorded provision as its status probe.

## Agent runtime and helper

The package application tree is immutable and runs directly from the MSIX; the
launcher does not extract, copy, hash, or repair it at runtime. Setup is the
only route that installs a Node.js runtime: `RunSetupCoreAsync` asks the guest
helper to install the runtime under the agent's own profile. The invoking
user's host runtime is never prepared, because nothing runs on the host.

The agent cannot execute the package's WindowsApps helper binary directly.
During setup, the launcher stages only `openclaw-session-host.exe` into the
shared session workspace. It does not stage the application tree. The helper
has distinct modes for launching a request, inspecting processes/listeners,
installing the agent runtime, and collecting requested diagnostics.

The helper also exposes an internal `--check-config <request-path>` mode used by
the launcher after packaged `openclaw` calls. It resolves the agent account's default
`.openclaw\openclaw.json` and classifies the file as `Absent`, `NotReady`, or
`StartupEligible` without starting Node.js or OpenClaw. This is deliberately a
file-only heuristic for the default OpenClaw-generated config:
`StartupEligible` means only that the file can be parsed and contains
`gateway.mode` set exactly to `local`. It does not resolve alternate profiles,
includes, environment substitution, secrets, plugins, bind/auth policy, ports,
or any other runtime dependency, and therefore does not claim that a gateway
will start or remain healthy.

On the local win-x64 NativeAOT publish, ten direct helper invocations took
53.5-108.9 ms (59.15 ms median), including process startup and file inspection
but excluding the MXC round trip. No invocation started Node.js or OpenClaw.

Opening the agent shell or using `clawctl pwsh --command` or `--file` installs
an ASCII `openclaw.cmd` shim in the shared workspace:
`RunPowerShellAsync` calls `InstallToolsAsync`. Setup alone does not guarantee
that shim exists. The shim reads its Node.js and entry-point paths from
environment variables rather than embedding profile paths, which avoids
batch-file code-page corruption.
The agent's persistent user PATH is prefixed with its bundled Node.js runtime
for independently started processes; each helper launch also supplies a
request-level PATH prefix. Together these ensure the agent resolves its own
bundled `node`, `npm`, and `npx`, not a device-installed Node.js.

`clawctl pwsh` starts an interactive shell in the owned session.
`--command` runs one PowerShell command string, while `--file` passes an
agent-visible script path and its argument vector without host-shell
flattening. All modes use the shared workspace as the child working directory
and build their environment using the interactive runtime policy so
terminal-related variables are retained when appropriate; redirected output
does not synthesize interactive defaults.

## Gateway lifecycle and recovery

`clawctl gateway-service start` starts the gateway inside the owned session;
`status` observes it without starting it; and `stop` stops it while leaving the
session and agent data intact. All require the setup record where appropriate.
The launcher does not impose an invented port: the OpenClaw configuration and
upstream default choose it unless configuration explicitly supplies one.

Setup configures sign-in recovery for the managed gateway but does not start a
gateway. Recovery is reconciled against the package identity and records its
own failures for status reporting. It is not evidence that a gateway is
currently healthy.

A stored gateway record is not liveness proof. Health requires successful guest
inspection, a live process with the recorded creation time, and a listener
owned by that process or one of its descendants. A missing record is
`NotStarted`; a stale record is `Stopped`; a live but non-serving recorded
process is `Unhealthy`; and failed inspection is `Unknown` to avoid starting a
second gateway beside one that could still be healthy.

The helper judges descendant ownership from one process snapshot per
inspection, taken only when a listener belongs to some other process. Such a
listener counts only when its owner's snapshot parent chain reaches the
recorded process, every process on that chain has a readable creation time,
and every parent started no later than its child. An owner whose chain never
reaches the recorded process is rejected without being opened. Measured inside
an isolated session with a live gateway, 26 listeners held by 20 processes, and
about 450 processes, evaluating each listener with its own snapshot and process
opens took 2.6-3.8 s; the single-snapshot evaluation returned the same gateway
port in 50-52 ms.

Both `clawctl status` and `clawctl gateway-service status` add the helper's
file-only config readiness whenever the managed gateway is not confirmed
running. The gateway-only command first observes gateway state, then starts
only an already-recorded session when necessary to reach the helper; it never
provisions a replacement or starts the gateway. Human output reports
`not configured`, `not ready`, `startup eligible`, `unavailable`, or `unknown`.
Structured output carries the same state under `gateway.readiness` with the
stable reason. A running gateway omits readiness and avoids the helper call.

After an `openclaw` child exits, the launcher uses the helper's file-only
readiness result before checking the managed gateway record and liveness. A
successful interactive call starts the managed gateway only for `NotStarted` or
`Stopped` with `startup eligible` readiness; unsuccessful or redirected calls,
and `Starting`, `Unhealthy`, or `Unknown` gateway states, remain silent. An
observed `Running` gateway is acknowledged. A failed or unverified start warns
on standard error and prints `clawctl gateway-service start` on its own retry
line; it does not change the OpenClaw exit code or acknowledge the guidance, so
the next eligible launch retries.

A successful automatic or manual start, or an observed running gateway,
acknowledges the guidance for the current Windows logon. Acknowledgement is
package-local and keyed to the Windows token authentication ID. Setting
`CLAWCTL_AUTO_GATEWAY_START` to `0`, `false`, `no`, or `off` restores the
previous start hint instead of automatically starting the gateway.

Agent entrypoint startup captures and restores console state and initializes
UTF-8 just as the control entrypoint does. Postflight rendering treats
foreground interactivity and the selected process stderr writer separately.
UTF-8 initialization can replace managed console writers; a previously captured
writer failing a later identity check is not evidence that its native handle
rejects `GetConsoleMode`. PTY selection uses the raw handle type and console
mode for all three streams, preserving separate file and pipe output. A native
console still requires successful VT enablement.

## Diagnostics and safe collection

`clawctl collect-logs [--output <path>]` creates a ZIP at the supplied path or
in package state by default. It collects host diagnostics and, when the owned
session can be reached, stages selected agent diagnostics through the guest
helper. A session collection failure is a warning rather than a reason to
discard available host diagnostics.

Agent paths are relative to the agent profile: OpenClaw logs come from
`AppData\Local\Temp\openclaw`, and configuration candidates are
`.openclaw\openclaw.json*`. The collector excludes SQLite databases and
authentication-profile files by name. Text entries are passed through
credential-shaped-value redaction before being added: JSON members, URL query
or fragment parameters, environment-style assignments, and HTTP authorization
header credentials. The manifest and the notes the command returns are
redacted the same way, because a failure note can quote raw executor or guest
output. Every redaction pattern runs non-backtracking, so crafted guest text
cannot stall collection. Collection is best-effort, and the ZIP manifest warns
that redaction is not a substitute for review before sharing.

The host also reads each gateway launch's output log and supervisor status
straight from the shared workspace. The files must be named for the recorded
session's generation, and reparse points are refused. Only the newest 10
launches are kept, each file cut to its last 1 MiB, because the workspace is
guest-writable. Reading these files does not start the session.

The helper accepts only host-named sources and writes collected files to the
shared workspace. Missing sources are reported as entries rather than treated
as a collection failure, which makes a bundle useful for partial or failed
setup. The collector does not enumerate arbitrary agent-profile files.

MXC failures reach the host log with the backend's own evidence: the error
code, the failing IsolationSession operation (for example
`IsoSessionOps.StartSessionAsync`), the native status, and any remediation
text. SDK failures retain their typed code, original cause, native status,
operation, and remediation; workload output is not parsed as an MXC error.
Both `clawctl setup` and `openclaw` log the evidence behind the support
decision (host build, runtime provenance, and backend probe result) before
acting on it, and `clawctl status` logs the MXC failure when the recorded
session cannot be started. The collector's manifest records the collecting
host's Windows build, package provenance, and component versions.

## Supported operational flow

1. Run `clawctl setup` after installing or updating the package to prepare the
   isolated agent session. It fails on a machine that cannot host a session,
   naming Windows Update or a newer Windows version as the remedy.
2. Run `openclaw <arguments>` for the upstream OpenClaw CLI, or
   `clawctl pwsh` for an interactive agent shell.
3. Use `clawctl gateway-service start`, `status`, `stop`, and `restart` for the
   managed gateway. Use `clawctl status` to inspect session ownership and state.
4. Run `clawctl collect-logs` when reporting a problem, then review the
   resulting ZIP before sharing it.
5. Run `clawctl teardown --force` to confirm removal of the owned isolated
   session and its data while retaining the installed package.

The `clawctl` command tree intentionally owns only these package-management
operations. Upstream commands such as `doctor`, `gateway`, and `uninstall`
remain `openclaw` arguments and are forwarded unchanged.
