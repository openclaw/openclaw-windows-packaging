# Session state and ownership

This installation records the isolated session it owns in a single versioned
file. That record is the only evidence of ownership.

## Why ownership is recorded, not detected

The MXC backend gives no reliable way to ask "did I create this?".

- `deprovision` is idempotent. Calling it on a sandbox this installation never
  created returns success, so a missing error proves nothing.
- Agent accounts outlive the sandboxes that created them. Two unrelated
  orphaned agent accounts already existed on the development machine before any
  of this work ran, so the presence of an account proves nothing either.

Anything that stops, deprovisions, or deletes therefore acts only on the
identity in this record. Machine state is never treated as an ownership signal.

## Where the record lives

`HostPaths` derives every writable path from one root, so nothing duplicates the
packaged-versus-unpackaged decision:

| Context | State root |
|---|---|
| Packaged | `%LOCALAPPDATA%\Packages\<package family name>\LocalState\OpenClawGatewayMSIX` |
| Unpackaged | `%LOCALAPPDATA%\OpenClawGatewayMSIX` |

A packaged process writes inside its own `LocalState`, so its state is removed
with the package and cannot collide with another installation's. The public and
internal MSIX have different package family names and therefore different state
roots, different session records, and different cleanup scope. Neither can
adopt or destroy the other's session.

`PackageIdentity.TryGetPackageFamilyName` returns null when unpackaged, because
that is an ordinary development configuration rather than a failure.

## What the record contains

`SessionRecord` persists the backend identity and the provision metadata later
phases need:

| Field | Why it is kept |
|---|---|
| `schemaVersion` | Lets a later build recognize, rather than misread, this format. |
| `sandboxId` | The full opaque backend identity, stored verbatim. |
| `applicationId` | The `PFN:` identity that provisioned it. |
| `agentUserName`, `agentUserSid` | The guest identity the backend created. |
| `workspacePath` | The OS-provided share used for launch requests. |
| `wireVersion` | The schema the session was provisioned against. |
| `createdUtc` | When this installation took ownership. |

The sandbox id is opaque. It carries a backend routing prefix and the
provisioning application identity, so it cannot be rebuilt from a session GUID
and is never normalized on the way in or out.

## Reading failures are distinct from having no session

`SessionStateStore.Read` never reports a damaged record as "no session":

| Fault | Meaning |
|---|---|
| `Missing` | Nothing has been recorded. A first session may be created. |
| `Unreadable` | The file exists but cannot be parsed. |
| `UnsupportedSchema` | A newer build wrote it. |
| `Incomplete` | Required values are absent. |
| `ForeignIdentity` | It belongs to another package identity. |

Only `Missing` permits provisioning. Treating an unreadable record as an absent
one would provision a replacement over a live backend session and abandon the
user's guest profile with it, which is precisely the data loss the distinction
exists to prevent. Every other fault is a recovery error the user is told about.

Writes are atomic: the record is written to a temporary file in the same
directory and moved over the target, so an interrupted write cannot leave a
truncated file that would later read as a damaged session.

## The lifecycle lock

`NamedSessionLock` serializes provision, start, stop, and deprovision across
processes, scoped to one user and package identity by a `Local\` named mutex.

It is deliberately **not** held for the lifetime of a foreground OpenClaw
invocation. Holding it there would let one long interactive run block every
other command indefinitely.

If the holding process dies mid-transition, the next caller receives the lock
rather than deadlocking behind a dead owner, and is responsible for reconciling
whatever partial state that owner left behind.
