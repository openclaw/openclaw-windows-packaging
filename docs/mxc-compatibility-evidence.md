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
The current `clawctl setup` check infers support from the Windows build number
alone; `--probe` is strictly better evidence and should replace it.

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

## Lifecycle notes for the session slice

- `stop` succeeds and leaves the provision intact; a subsequent `exec` fails
  with `backend_error` rather than silently restarting the session.
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
