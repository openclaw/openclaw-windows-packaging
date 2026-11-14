---
name: windows-agent-handoff
description: Read before advice/actions on all human profile folders (e.g. Photos), delivery, GUI or sign-in under isolation, even observational; not private headless work.
metadata: {"openclaw":{"os":["win32"],"requires":{"env":["CLAWCTL_GATEWAY_ISOLATION"]}}}
---

# Windows Agent Handoff

The agent runs as its own Windows account in an isolated session. The human and
the agent share one folder: the agent's `USERPROFILE\Shared`, for example
`C:\Users\<agent user>\Shared`. Setup creates it by default and its ACLs give both
accounts access, so the human can always open it. Copying a file there is an
ordinary local copy; there is no separate delivery step to confirm.

## 1. Establish the facts

- Confirm local Windows execution and exactly `CLAWCTL_GATEWAY_ISOLATION=enabled`
  from supplied facts or an authorized check of that value only. If that evidence
  is missing, ask where execution occurs; don't infer it from Windows alone.
- A fact stated for the current request overrides a general or default statement.
- Keep supplied results separate from checks you run now: say "According to the
  supplied result" rather than "I checked" unless a current tool checked it.
- Observational, hypothetical, or status-only requests get an explanation from the
  supplied facts: no filesystem checks, writes, copies, or uploads. State the next
  action you would take; don't turn it into a question. Reading this skill is not
  checking a file.
- The agent has no administrator access and cannot elevate, open an elevated
  Explorer, or create, start, or repair its own session. When asked, say it can't.

## 2. Human folders

"My" Desktop, Documents, Downloads, Pictures/Photos, Music, Videos, or any other
profile folder or known-folder alias means the human's, not the agent's
same-named folder. The agent cannot write any of them. Redirection, cloud sync,
another drive, a translated name, or the user's permission does not change that.
Never probe or resolve a human profile, request profile access, or change ACLs.

<!-- Provenance: Shared without asking, no receipt caveats, offering only an
available node, and the missing-Shared setup fix come from
https://github.com/openclaw/openclaw-windows-packaging/pull/259, which replaced
the destination-choice rules from #112 and records the paired evidence. -->

When a request names a human folder:

1. Say you can't write there, then do the supported thing: copy the file to
   Shared and say exactly where it is, for example "I don't have access to write
   to your Desktop folder, so instead I copied it to
   C:\Users\<agent user>\Shared\report.txt." Shared needs no selection or
   approval, and the request itself is the go-ahead: don't ask whether to go
   ahead with Shared, list other routes, or invite the user to say go.
   Observational mode only defers the copy; state it as the next action, for
   example "Next, I'd copy it to C:\Users\<agent user>\Shared\report.txt." End
   there, with no closing question or offer about Shared.
2. If the user asked for an in-chat attachment as the fallback and an attachment
   is available or already accepted, use it and say so: "I couldn't save to your
   Downloads, so I've attached it here." Use Shared only if the attachment had a
   problem.
3. Only when a connected node is available, add an offer after the Shared copy
   to send the file to that node's own folder, asking for confirmation for the
   node only: "I might also be able to send it to [node]'s Desktop folder. Want
   me to?" A node is available when the user mentions a connected node, or the
   supplied facts or the actual `nodes` tool's `status` show one. Never invent
   a node or a node check; when no node is available, don't mention nodes.
   Sending needs an actual node file-write tool, such as File Transfer's
   `file_write`, and that node's path policy; don't claim it worked until it
   has.
4. A folder the user supplies, or asks you to create, that both accounts can
   access is fine to use. Don't ask the user to name other folders or provide
   sharing evidence.

## 3. Copying to Shared

For an authorized copy, or when describing one:

1. Resolve the agent's existing Shared with an authorized local tool in the
   isolated session:

   ```powershell
   (Resolve-Path -LiteralPath (Join-Path $env:USERPROFILE 'Shared') -ErrorAction Stop).Path
   ```

   Use the agent's current `USERPROFILE`, not a human profile, a remembered
   account, the OpenClaw workspace, or a host path.
2. Copy, don't move, only the requested files to a destination inside Shared,
   with no parent traversal or links redirecting outside it. Don't overwrite an
   existing file without approval; choose a nonconflicting name instead.
3. Re-read the copy, compare its bytes or hash with the current source, and
   report the exact path. Keep the original.

Report it plainly: the copy is in Shared at its exact path, the human folder was
not written, and the original is unchanged. The user can open Shared, so don't
add caveats about delivery, receipt, download, or recipient access, and don't
offer to verify them.

**Missing Shared.** Shared is created by default, so if it is missing or can't be
resolved, setup is broken. Say so, say nothing was copied and the human folder is
unchanged, and point to the user-side fix: run `clawctl status`, then
`clawctl setup`, from the user's normal terminal, outside the agent. Don't create,
repair, or replace Shared from inside the agent, and don't fall back to another
profile, the agent's own folders, Public Documents, `PUBLIC`/`TEMP`, or a new
root. If a connected node is available, offer it, asking first.

## 4. In-chat attachments

A new attachment needs the current client's documented capability, an actual
attachment tool, and the exact current file. Without a tool, say in-chat
attachment isn't available and offer to copy the file to Shared instead; don't
copy silently, and don't claim "I can send it" from a skill or connection. An old
attachment card, the same filename, or a local path is not this revision.

An attachment already accepted for the exact current revision is done: report it
("I've attached it here") without uploading again or offering to resend. A later
explicit request to resend or add a recipient needs current capability again.

## 5. What can leave the agent

The human should have full visibility into what the agent has. Copy the files the
user asks for, including a whole workspace, with the standard filtering: never
export the credential cache or credential databases, and leave out secrets,
tokens, auth profiles, and private configuration, resolving anything doubtful
first. Use Shared for it. Never use Public Documents, `PUBLIC`/`TEMP`, or an
Everyone ACL grant.

## 6. The agent's own folders

When the user asks for output in the agent account's own folders, such as its
Desktop, Documents, Pictures, Music, or Videos, that's allowed. Resolve only that
agent folder, verify the content when executing, and report the actual path, for
example "created at C:\Users\<agent user>\Desktop\notes.txt, which is the agent
account's Desktop, not yours." A blocked human-folder request alone doesn't call
for an agent-folder copy; use Shared. If the user also asked for a private copy,
report where each copy is. Don't guess identity from an account name or path
shape. Ordinary private headless work stays in its normal workflow, without
forced export or isolation warnings.

## 7. GUI, sign-in, and remote UI

Windows and dialogs the agent opens belong to the isolated agent session, not the
human's desktop: the human can't see or type into them. Never launch or offer one
for human viewing or input.

- **Agent-only GUI** is neither banned nor guaranteed. It may not work at all, and
  some apps crash or behave oddly. When authorized, establish the actual
  automation tool and session, verify the real result rather than process
  creation, say plainly if it didn't work, and close only windows or processes
  you started.
- **Sign-in:** when the service is unknown, explain the invisible local dialog,
  ask "Which service are you signing into?", and stop, before flow examples,
  CLI/device-code/OAuth advice, or URLs. Once known, obtain official instructions
  before choosing its documented CLI/headless/device-code/text flow and an
  actually supported input route; if instructions are unavailable, obtain or
  request them and wait. Never guess flows, URLs, or codes. Relay a safe
  verification URL only if provided, and sensitive verification material only
  through a supported private route to the requester. Never ask for passwords,
  cookies, tokens, recovery codes, or MFA secrets in chat, and never bypass MFA,
  consent, or security policy. Wait for the user, then confirm with service
  status or account evidence without printing secrets. User confirmation, a
  window, or a code is not authentication proof.
- **Remote or node UI:** a node has its own desktop and filesystem. Before acting,
  establish the selected node, its actual tools, and the user's confirmation.
  Don't assume `host` is the human's desktop, and don't pass an agent-local path
  as a node path.

## 8. Report

Report what actually happened, file by file for partial work. Command success
alone proves no GUI operation or sign-in. Don't invent a tool, route, or check.