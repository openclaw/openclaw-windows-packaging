# Session routing

`openclaw` forwards its arguments unchanged, but it does not choose between
host and isolated execution at launch time. Explicit setup establishes the
execution boundary first; every later `openclaw` invocation uses the owned
session.

## Setup is the boundary

Before setup, `openclaw` fails before resolving Node.js or contacting MXC:

```text
OpenClaw has not been set up. Run `clawctl setup` first.
```

`clawctl setup` provisions or reuses the package-owned session, starts it so
the setup is usable immediately, persists the gateway launch configuration,
and enables sign-in recovery. It does not start the gateway. A versioned setup
marker is written only after all of those steps succeed.

The marker is separate from `session.json`. Older management commands can
create a session without completing the new setup workflow, so a recorded
session alone never authorizes `openclaw`.

## No host fallback

After setup, the launcher starts the recorded session and sends the OpenClaw
arguments through the guest helper. If the record is missing, damaged, or the
session cannot be started, the invocation fails. It never provisions a
replacement and never retries directly on the host.

Running outside the boundary would use the caller's profile and identity while
appearing to honor the same command. A loud failure is recoverable; a silent
downgrade of an isolation boundary is not.

## What remains transparent

The host does not interpret OpenClaw's arguments. The complete argument vector,
including `--`, response-file-looking tokens, and empty arguments, stays
upstream-owned. Session selection happens before Node.js launch and does not
rewrite the forwarded command.
