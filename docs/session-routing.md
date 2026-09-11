# Session routing

`openclaw` forwards its arguments unchanged. What this document describes is
*where* those arguments run: inside the isolated session this installation
owns, or directly on the host.

## The decision

Routing is decided once per invocation, before Node.js is launched, from three
inputs:

| Input | Source |
|---|---|
| Mode | `OPENCLAW_SESSION` |
| Package identity | The running MSIX package family name |
| Backend readiness | The pinned MXC runtime's own `--probe` |

| `OPENCLAW_SESSION` | Meaning |
|---|---|
| unset | Automatic. Use a session wherever the backend reports support. |
| `0`, `false`, `off`, `no` | Disabled. Always run on the host. |
| `1`, `true`, `on`, `yes` | Required. Fail when a session is unavailable. |

An unrecognized value is an error rather than a silent "off". A typo in a
variable that selects an isolation boundary must not quietly disable it.

## Why the default is on

Isolation is the point of this package. Making it opt-in would mean the
protection only reaches users who already know it exists, which is exactly the
population that needs it least. So a machine whose backend reports support gets
a session without being asked.

Support is *measured*, not predicted. The runtime's own non-mutating probe
answers the question; the Windows build number is only a fallback when the probe
cannot be reached, because a build number predicts support rather than
establishing it.

## Why an unsupported machine runs directly

A machine without the isolation backend is not failing — it simply cannot offer
the capability. Refusing to run OpenClaw there would break every user on an
older Windows build to protect a feature they cannot have. The decision and its
reason are logged, so the difference is visible rather than invisible.

This is the one place the distinction matters most: *unavailable* is reported as
a routing outcome, while *broken* is reported as an error.

## Why a required session never falls back

When `OPENCLAW_SESSION=1` is set, or when a session was selected and the backend
then fails, execution stops. It does not retry on the host.

Falling back would be the worst possible outcome: the user asked for their work
to run under a separate identity, with a separate profile, and would instead
get it silently running as themselves, against their own profile, with no
indication that the boundary they asked for was never there. A loud failure is
recoverable. A silent downgrade of an isolation boundary is not.

## What is decided elsewhere

Routing chooses the execution target. It does not decide whether a session
exists — that is the coordinator's job, described in
[session-state.md](session-state.md) — and it does not interpret OpenClaw's
arguments, which stay upstream-owned in every path.
