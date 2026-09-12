# MXC compatibility evidence (gates G1-G5)

Evidence recorded against `@microsoft/mxc-sdk` **0.8.0** `wxc-exec.exe` (x64)
and the Windows **IsolationSession** backend.

| Item | Value |
|---|---|
| Machine | Local development workstation (`paulcam-tr`) |
| Windows build | 26686.1000 (pinned minimum is 26340.9212) |
| Wire schema | `0.6.0-alpha` |
| Session | Provisioned, started, exercised, stopped, and deprovisioned |
| Residue | None. The agent account and its profile were removed; verified after teardown. |

Every observation below came from executing the pinned runtime. Nothing here is
inferred from source reading.

## G5: supported machine

`wxc-exec.exe --probe` reports host capability without spawning a sandbox:

```json
{ "tier": "base-container",
  "probes": { "isolationSessionAvailable": true, "baseContainerApiPresent": true } }
```

`--probe` is non-mutating and is the correct readiness signal for a host check.
`clawctl setup` now uses it as the primary support verdict and falls back to the
Windows build comparison only when the runtime is missing or the detector itself
cannot run, reporting which evidence it used. Verified end to end against a
NativeAOT `clawctl setup`, which reported `Backend tier: base-container` from
this live output.

## G1: published runtime contract

**Confirmed as implemented.** Envelope construction, base64 config transport,
phase names, the required unrestricted-network acknowledgement, and the
provision result shape all match `MxcWireProtocol`.

Corrections this proof forced:

| Finding | Consequence |
|---|---|
| Post-provision phases must **not** carry `containment`. Sending it fails with `malformed_request`: "the backend is fixed at provision and later phases route by 'sandboxId'". | Already correct in `BuildPhaseEnvelope`; now covered by a test citing the real message. |
| Three unmodelled error codes exist: `malformed_request`, `unsupported_containment`, `backend_error`. | Added to `MxcErrorCode` and `Classify`. |
| Error envelopes carry `operation`, `nativeCode`, and `remediation` beyond `code`/`message`. | Added to `MxcErrorEnvelope`; `remediation` and `nativeCode` are surfaced in the exception message. |
| A sandbox id is `iso:<base64url payload>`; `appId` travels inside that payload, not as a separate segment. | Opaque-id handling was already correct. |

Observed error contract, all delivered as an `{error}` envelope on **stdout**
with a nonzero exit code:

| Request | Code |
|---|---|
| `network` omitted at provision | `policy_validation` |
| `network.defaultPolicy: "deny"` | `malformed_request` (valid values are `allow`/`block`) |
| `sandboxId` without a `prefix:` | `malformed_id` |
| `sandboxId` with prefix `zzz:` | `unsupported_containment` |
| `appId` of 304 characters | `policy_validation` ("at most 256 characters") |
| `appId` containing U+0001 | `policy_validation` ("must not contain control characters") |
| `exec` after `stop` | `backend_error`, native `0x80070520`, with remediation text |

Parsing the stdout envelope before consulting the exit code is therefore
correct: the code alone never names the failure.

## G2: packaged file access — **PASS**

From inside a started session, running as the agent user:

| Target | Result |
|---|---|
| Device-installed Node on `PATH` | `v24.18.0` |
| A real immutable package file under `C:\Program Files\WindowsApps` | Read, 13131 bytes |
| Host repository path (the caller's directory) | Readable |
| `appId` in `PFN:<packageFamilyName>` form | Accepted |

Identity and isolation behaved as the plan requires:

```text
whoami            -> paulcam-tr\c8-h2
USERPROFILE       -> C:\Users\C8-H2
ephemeralWorkspace-> C:\Users\C8-H2\Shared   (read/write from the host)
C:\Users\paulcam\.openclaw -> ENOENT
```

The agent receives a **fresh isolated profile** and cannot see the interactive
user's OpenClaw state, which is exactly the confirmed clean-first-run policy.
No host-folder mapping was requested or needed.

## G3: literal execution — **FAILS without a guest helper**

`wxc-exec` accepts a command as an argv vector after `--`, which at first
appears to avoid command-string quoting. It does not: the vector is flattened
and dispatched through `cmd.exe`. Two reproducible **silent** corruptions:

1. **Environment expansion.** A single argument `%USERPROFILE%` arrived as
   `C:\Users\C8-H2`. Any OpenClaw argument containing `%...%` is rewritten
   without error.
2. **Quote-driven argument injection.** Passing `['q"x', 'a&b']` arrived as
   `['q"x', 'a']`, exit 1, with `'b' is not recognized as an internal or
   external command`. A double quote in one argument breaks quoting for a
   later argument, truncating it and executing its remainder as a command.

Individually, spaces, quotes, `!`, trailing backslashes, `|`, `&`, `^`, `;`,
`>`, parentheses, empty arguments, and Unicode all survive; the defects appear
with `%` and with cross-argument interaction. Character-level allowlisting is
therefore not a viable mitigation.

### Mitigation, proven

Passing argv as **data** rather than command text preserves it exactly. A
helper in the guest read a JSON argv file from the ephemeral workspace and
spawned the target shell-free:

```text
expected ["plain","has space","quote\"inside","%USERPROFILE%","!BANG!",
          "trailing\\","pipe|amp&caret^","","ué☃","a>b"]
actual   ["plain","has space","quote\"inside","%USERPROFILE%","!BANG!",
          "trailing\\","pipe|amp&caret^","","ué☃","a>b"]
```

Byte-for-byte, including the unexpanded `%USERPROFILE%` and the empty argument.

This validates `src\OpenClaw.SessionHost` as a requirement, not a convenience:
the MXC command line must launch only a controlled helper, and OpenClaw's real
arguments must never be interpolated into it.

Also confirmed for the execution path:

- **Exit codes propagate** through both layers (a child exiting 42 surfaced as
  42 from `wxc-exec`).
- **Working directory is not inherited.** Execution defaults to
  `C:\Windows\System32`. The helper must set the caller's directory explicitly;
  doing so was verified against the host repository path.

## G4: background and task activation — not yet attempted

Out of scope for this proof; it belongs with the managed-gateway slice.

## G6: detached gateway lifetime — **PASS**

Measured because it decides the shape of the managed-gateway slice: if a
detached process cannot outlive the one-shot `exec` that started it, the host
must keep a long-lived supervisor alive for as long as the gateway runs. It
can, so it does not.

Test-owned session, agent `C2-G8`, workspace `C:\Users\C2-G8\Shared`, since
deprovisioned; the account, `C:\Users\C2-G8`, and the child process were all
verified gone afterwards.

A guest launcher script started a hidden child through `Start-Process` and
recorded its PID and process start time. The launching `exec` returned
immediately with exit 0.

| Observation | Result |
|---|---|
| Child alive 8s after the launching `exec` returned | **Yes** (pid 147252) |
| Recorded start time still matches | Yes |
| Loopback listener the child opened | Still listening (port 57908) |
| Child alive 23s later, via a second independent `exec` | **Yes**, same PID, still listening |
| Child identity | `paulcam-tr\c2-g8` — the isolated agent, not the caller |
| After sandbox `stop` then `start` | **Gone.** `alive:false`, listener closed |

Two consequences for the gateway slice:

1. **No host-side supervisor is required.** The gateway can run detached inside
   the session, and a later `exec` can inspect and control it. This matches the
   internal precedent, which proves ownership from a persisted PID *and* process
   creation time rather than keeping an owner process alive.
2. **`stop` is a real boundary.** Stopping the sandbox terminates detached work
   without notifying anything, so recorded gateway state can outlive the process
   it describes. Stale owned state must be reconciled before a restart, and
   status must never infer liveness from the record alone.

The start-time check is not ceremony: it is what distinguishes the recorded
process from an unrelated one that inherited its PID after Windows reused it.

## Task Scheduler round trip — **PASS, with one correction**

Measured on this workstation, unelevated, against the inbox `schtasks.exe`,
using a test-only package family name so the task could not collide with any
real installation. The task was registered, queried, reinstalled, and deleted;
its action was never triggered, and nothing was left behind.

| Observation | Result |
|---|---|
| The generated task XML is accepted | ✅ registered from a UTF-16 file |
| A missing task is distinguishable from a refused read | ✅ `ERROR: The system cannot find the file specified.`, exit 1 |
| `NotInstalled` → `install` → `Ready / TaskScheduler` | ✅ |
| `status` immediately afterwards | ✅ `Ready`, no drift reported |
| A second `install` | ✅ no change, no re-registration |
| `uninstall` → `NotInstalled` | ✅ task, launcher, and fallback all gone |

**The correction.** Task Scheduler does not store back what it was given.
Registering a logon trigger whose `UserId` is a SID returns a trigger whose
`UserId` is the *account name*, while the principal keeps its SID:

```text
registered: <UserId>S-1-12-1-…-467001824</UserId>
read back : <UserId>REDMOND\paulcam</UserId>
```

Comparing those as text reports drift on every probe. That would have made
`status` permanently wrong and made `install` re-register on every run — and no
test using a fake scheduler could have caught it, because the fake returns
exactly what it was handed. Account identity is therefore compared by resolved
SID, falling back to a literal comparison when an account cannot be resolved.

Two further consequences are already handled: the definition is compared field
by field rather than as XML text, because Task Scheduler also reorders elements
and fills in defaults; and a definition that simply omits a setting parses to
Task Scheduler's own default, which is the opposite of what this build wants
for battery gating and the execution time limit.

## Detached gateway: in-session ownership — **PASS, with one correction**

Measured with the published NativeAOT helper (`openclaw-session-host.exe`,
`win-x64`) on this workstation, using a stand-in application that opens a
loopback listener and stays up. This exercises the guest helper directly; it
does not depend on an MXC session, so it is evidence about the helper's own
contract rather than about the backend.

Note on naming: the in-session intermediate is called a *supervisor*, but it is
not the host-side supervisor that the detached-lifetime gate removed. Nothing on
the host stays alive. It is the in-session equivalent of the internal build's
launcher script: the process whose identity is recorded, with the gateway as its
child.

| Observation | Result |
|---|---|
| The launching execution returns immediately | ✅ exit 0, gateway still starting |
| It reports the supervisor's identity and creation time | ✅ |
| The application's output reaches a real log | ✅ redirected by the supervisor |
| A later, independent inspection finds it | ✅ `processFound`, `startTimeMatches` |
| The listener belongs to the recorded process tree | ✅ `listenerOwned` |
| A recorded creation time that does not match | ✅ rejected, and its listener is not credited |
| A stop whose recorded identity does not match | ✅ **nothing is terminated**; the process stays up |
| A stop whose recorded identity matches | ✅ process gone, port released, no orphan left |
| After the supervisor is gone | ✅ reported as not found |

`listenerOwned` is the part that could not be taken on trust. It resolves the
port's owning process through the extended TCP table and walks the parent chain,
so a gateway that is a *child* of the recorded process is recognized while an
unrelated program that merely holds the port is not.

**The correction.** Killing the recorded supervisor left the application
running and still listening:

```text
before the fix: processFound:false  portListening:true   <- orphan holds the port
after the fix : processFound:false  portListening:false
```

Nothing owned that process: a later start would have found the port taken by
something it could not prove was its own and could not safely stop. The
supervisor now places the application in a kill-on-job-close job, so the
application cannot outlive the identity recorded as owning it. The job is
created before the application starts, and an application that cannot be joined
to it is stopped rather than left running.

One consequence is deliberate and remains. A supervisor that is killed never
writes its own final status, so the status file can still read `running` after
the process is gone. Health therefore never rests on that file: it requires a
live process with a matching creation time, and the status is reported only as
corroborating detail.

## Lifecycle notes for the session slice

- `stop` succeeds and leaves the provision intact; a subsequent `exec` fails
  with `backend_error` rather than silently restarting the session.
- **`start` is idempotent.** Measured directly, because session reuse depends
  on it: the coordinator cannot ask whether a session is running, so it issues
  `start` on every invocation. Against a second test-owned session
  (`T5-Y2`, deprovisioned; no residue), three consecutive `start` calls on one
  sandbox all succeeded, and `exec` afterwards returned exit 0 with the
  expected stdout. A `start` issued after `stop` also succeeded and restored
  execution (`exit=0`). Reuse and recovery from a stopped session therefore
  need no liveness query and no tolerated error code.
- `deprovision` is **idempotent**: a second call returned `{"result":{}}`
  rather than `stale_id`. Absence of an error is therefore not proof that this
  caller owned the resource, and ownership must be tracked locally.
- After `deprovision` the agent account and `C:\Users\<agent>` were both gone.
- **Orphaned agent accounts do accumulate.** Two unrelated agent-pattern
  accounts (`B2-X4`, `T4-K6`, both created 2026-09-09) and their profiles were
  already present on this machine before the proof and remain after it. They
  were left untouched. This is direct evidence that a deprovision can be missed
  and that this package must track ownership locally rather than inferring it
  from what exists on the machine, and must never clean up by naming pattern.
