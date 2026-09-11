# Managing the gateway

`clawctl gateway-service` runs OpenClaw's gateway in the background, inside this
installation's isolated session, and brings it back when you sign in.

```text
clawctl gateway-service install    Start the gateway and restart it at sign-in.
clawctl gateway-service status     Show the gateway and its sign-in recovery.
clawctl gateway-service start      Start the gateway if it is not running.
clawctl gateway-service stop       Stop the gateway, keeping its data.
clawctl gateway-service uninstall  Stop the gateway and remove sign-in recovery.
```

`status` is read-only. It never starts a gateway, provisions a session, or
registers anything.

## Why `gateway-service` and not `gateway`

OpenClaw owns `openclaw gateway run`. A host command called `gateway` would
shadow it, so the host verb that manages the background gateway is spelled
`gateway-service`. `openclaw` itself stays transparent: every argument is
forwarded unchanged, and none of these commands are reachable through it.

## What `install` does

`install` is both "start it" and "keep it running", because starting a
background gateway that disappears at the next sign-in is rarely what anyone
wants. It succeeds only when both halves succeed; a half-configured
installation exits non-zero and says which half failed.

It registers a logon task that runs a small generated launcher script, which in
turn runs `clawctl gateway-service start`.

```text
logon task  ->  gateway-launcher.cmd  ->  clawctl gateway-service start
```

The indirection is deliberate. The launcher can be rewritten without
elevation, whereas changing the task's own action needs another registration
and another consent prompt. If the task cannot be registered at all, a Startup
folder entry calls that same launcher, so the two can never drift apart. That
fallback is always reported, never silently substituted.

The task is registered as you, unelevated, and runs with least privilege. It is
not gated on AC power and has no execution time limit, so the gateway is not
killed after three days or refused on battery. A second sign-in does not start a
second gateway.

## What `status` can tell you

| Reported | Meaning |
|---|---|
| not started | This installation has never started a gateway. |
| running | A live, identity-matched gateway is serving. |
| not running | It was started and is gone. Ordinary; the session may have stopped. |
| running but not serving | The process is alive but nothing is listening. The log is named. |
| could not be determined | Nothing could be observed. **Not** the same as stopped. |

That last row matters. If the gateway's state cannot be established, `start`
refuses to start another one rather than leaving two processes contending for
one port, and `stop` refuses to terminate anything rather than acting on an
identifier it has not verified.

## How ownership is established

The record says what was started. It never says what is running: stopping the
session terminates background work without notifying anything, so a record
routinely outlives the process it describes.

Every claim that the gateway is running is re-established inside the session,
and all of the following must hold:

- a process with the recorded identifier exists;
- its creation time matches the recorded one, because Windows reuses process
  identifiers and an identifier alone would eventually name a stranger;
- something is listening on the gateway's port;
- that listener belongs to the recorded process or one of its descendants.

An open port on its own proves only that *something* is listening. Any program
in the session could have opened it.

## Sign-in recovery you turned off stays off

If you disable sign-in recovery, a later `start` does not quietly re-enable it.
`status` says so, and `install` is how you ask for it back.

## What `stop` and `uninstall` keep

`stop` ends the gateway. `uninstall` ends it and removes sign-in recovery.
Neither touches the isolated session, its guest profile, or your data — use
`clawctl session remove` to discard those, which warns that it is destructive.

## Troubleshooting

Drift is reported together with the command that repairs it, rather than
repaired silently behind an unexpected elevation prompt. If `status` says
sign-in recovery needs attention, it names `clawctl gateway-service install`.

A task that cannot be *read* is reported as unreadable, not as missing.
Re-registering on a refused read would be an unbounded retry whose write is as
likely to be refused as the read was.

The gateway's output is written to a log inside the session; `status` names its
path whenever the gateway is running or unhealthy.
