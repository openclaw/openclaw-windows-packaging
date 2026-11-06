---
name: windows-agent-handoff
description: Handle user-folder requests (Desktop/Documents/Downloads), file delivery, GUI, or sign-in from an isolated Windows agent; not private headless work.
metadata: {"openclaw":{"os":["win32"],"requires":{"env":["CLAWCTL_GATEWAY_ISOLATION"]}}}
---

# Windows Agent Handoff

## Operating contract

| Concern | Rule |
|---|---|
| Use when | A task names the human's Desktop/Documents/Downloads or needs human viewing/input, sign-in participation, user-session GUI, or file delivery. |
| Do not use when | Ordinary headless work stays inside the agent session, or an established remote-node workflow needs no local-to-user handoff. This skill does not provision sessions or administer the host. |
| Required inputs | Execution location, launcher isolation report, intended user action or exact deliverable, available client/tool capabilities, and any selected destination. |
| Mutation boundary | Classify the destination before writing. The agent has no permission to write the human's profile folders, even when requested. Preview supported handoffs; never weaken isolation, change ACLs, collect credentials, or export private workspaces. |
| Done when | The requested user action or exact-file delivery has observable evidence; otherwise report the blocker and next supported step without claiming completion. |

## Workflow

1. Establish where the action will run.
   - Confirm that the local execution environment is Windows and its
     `CLAWCTL_GATEWAY_ISOLATION` value is exactly `enabled`. Inspect only the
     required value, not the whole environment.
   - A tool target named `host` or `gateway` is not proof of the interactive
     user's session. A connected chat, browser, or node is not proof of GUI
     capabilities, authorization, or filesystem access.
   - If the report is absent, invalid, or unavailable, do not infer isolation
     from Windows alone. Ask for execution-location evidence before relying on
     this workflow; do not provision or repair the session.
   - **Check:** the action's execution location and applicable boundary are
     established, or the uncertainty is explicit.

2. Classify the requested outcome before choosing a path or writing a file.
   - For "my Desktop", "my Documents", "my Downloads", or a human-profile path,
     apply **Human folders and agent-local output** below before any path lookup.
   - This file contains the complete GUI, authentication, and file-handoff
     procedures, including refusals and recovery. No branch-file read is needed.
   - Use the applicable sections below; when GUI/authentication and file
     delivery are both needed, follow both. Unsafe requested actions remain
     prohibited during read-only preparation.
   - Keep unrelated headless work in the agent session; do not repeat its isolation explanation every turn.
   - **Check:** the route matches the user's requested outcome, not merely a
     convenient local operation.

3. Execute only the authorized part.
   - Preserve private originals, caches, repositories, credentials, and
     unrelated files. A supported client handoff does not grant broader
     filesystem or remote-node access.
   - If the user declines a destination or action, stop that action. Missing
     capabilities, permission denial, partial completion, and unavailable
     client delivery remain explicit failures; do not invent a fallback.
   - **Check:** each side effect stays within the request, and incomplete work
     is distinguishable from success.

4. Verify and report the observable outcome.
   - Separate execution, recipient filesystem access, and client attachment/open/download
     evidence; none implies the others. Report completion, evidence, and unverified access/delivery.
   - When blocked, name the next supported user action.
   - **Check:** local command success alone proves no visible window, sign-in, or delivery.

## GUI and human participation

Local dialogs and browser windows belong to the isolated agent session, not
the user's interactive desktop. Do not launch or offer a local window for
human viewing or input, even if asked to open a sign-in dialog. Explain the
limitation only when it affects the task.

Authorized agent-only GUI work is allowed when no human viewing or input is
needed. Verify a suitable automation tool is actually available, establish its
execution session, and check its result. Creating a process is not proof that
automation worked. Close only task-created windows or processes when finished;
do not terminate unrelated work.

**Check:** know the viewer/operator and actual tool capabilities before GUI work.

## Authentication

1. If the service is unknown, explain that a local sign-in dialog cannot reach
   the user's desktop, ask one concrete question such as **"Which service are
   you signing into?"**, then wait for the answer. End the turn there; do not
   propose an authentication flow or request secrets.
2. For a known service, prefer its documented CLI, headless, device-code, or
   text flow. If official sign-in instructions are unavailable, obtain them
   or ask the user for the official instructions and wait before proposing a
   flow. Do not substitute guessed URLs, device codes, or flows, or promise
   that a local window will reach the user.
3. Give concise steps for the user's own desktop or a supported connected
   client. Relay a safe verification URL only when the documented flow
   provides it; relay sensitive verification material only through a supported
   private route to the requesting user.
4. Do not ask for passwords, session cookies, access tokens, recovery codes,
   or MFA secrets in chat. Never fabricate a device-code flow, bypass MFA or
   consent, disable isolation, or change security policy.
5. Wait for the required user action. User confirmation alone is not proof of
   agent-side authentication: use the service's supported status or account
   check without printing secrets. If it fails or expires, report the actual
   failure and offer the documented retry.

**Check:** the intended authenticated state has service evidence, or remaining
participation/blockers are explicit. An opened window or code is not proof of sign-in.

## Remote or user-session UI

A connected node may have its own desktop, filesystem, and automation tools.
It does not grant this agent human-profile write access or make a gateway-local
path available there. Do not use a node to reroute a blocked human-folder write.
Before using it for a separately authorized action, establish the selected node,
execution location, available capability, and authorization for the exact action. Do not transfer an
agent-local file path or assume that `host` means the user's desktop.

Use a verified, authorized UI route when available; otherwise provide user-side
steps or a supported headless route. Do not install remote-control tools,
change ACLs, elevate Explorer, or escape isolation to make a GUI visible.

**Check:** selected-environment evidence supports user-visible results; connectivity does not.

## Human folders and agent-local output

"My Desktop", "my Documents", and "my Downloads" mean the human user's folders,
not the isolated agent account's similarly named folders. The agent has **no
permission to write the human's actual folders**. This is a hard boundary, not a
missing path or an approval opportunity. Do not probe human profiles, resolve
human known folders, test access, request broader permissions, change ACLs, or
route the write through a connected node. Do not resolve the agent's Desktop,
Documents, or Downloads and silently describe it as the user's destination.

1. State the limitation before any write: **"I can't write to your Desktop from
   this isolated session."** Use the requested folder name for other folders.
2. Offer only the existing Shared-folder contract or an actually supported
   download/attachment. If the request has not authorized an alternative, ask
   which available route the user wants and wait; do not ask for human-folder
   access or permission to bypass isolation. If neither route is available,
   report that blocker without claiming the file was delivered.
3. Once a supported route is selected, follow its procedure below and name its
   exact outcome. A file delivered for the user to save themselves is not a file
   written to their Desktop/Documents/Downloads.

An explicit request for **agent-local** output is different: writing a private
file in the agent's own Desktop/Documents/Downloads is permitted within the
task's scope. Resolve only the agent's own selected folder, verify the exact
file, and say **"Created in the isolated agent's Desktop, not your Desktop; it
has not been delivered to you."** Include the actual path. Do not invoke Shared
or an attachment unless delivery is also requested. Ordinary private headless
work likewise needs no handoff or repeated isolation warning.

**Check:** the human-folder write remains blocked, or an explicitly requested
agent-local result is labeled as such; no silent destination substitution occurs.

## Deliverable and channel

Identify the exact current file, intended recipient, and requested outcome:
in-chat attachment, client download/export, or recipient-accessible filesystem
copy. Verify that the file exists and contains the intended output. Do not
substitute an old attachment, filename, path, saved copy, or local file link
for delivery.

Keep scratch files, dependencies, repositories, caches, and private originals
private. Hand off only requested nonsensitive deliverables, never a whole
workspace or credential database. Resolve possible secrets or private
configuration before exporting.

**Check:** the verified current artifact and intended delivery route are known.

## Client attachment or export

Use the current client's documented attachment/export capability and actual
tool schema. Do not invent an attachment tool or claim delivery from the
existence of a local file.

If unavailable, explain that in-chat delivery is blocked and offer a supported
alternative. Do not silently switch to a filesystem copy. Verify that the
attachment/export result identifies the current artifact; verify recipient-side
open/download and exact bytes when evidence is available. Otherwise distinguish
accepted attachment submission from verified receipt. Say **"Submitted the file
for download/attachment; I did not write to your Downloads."** when that folder
was requested, adjusting the folder name and submission/receipt claim to the
actual evidence. Never claim where the user's browser saved it without evidence.

**Check:** evidence matches the current file and requested channel, or the limitation is explicit.

## Filesystem copy

1. Prefer a host-reported shared root or an approved user-selected existing
   supported shared root. For user selection, establish the exact selected root
   and concrete evidence that it is a supported shared destination; a folder
   name, user permission, or agent write access alone is insufficient. Selection
   never grants human-profile access: reject those paths without probing them.
   A host-reported path alone does not prove recipient access. Do not run host
   lifecycle commands inside the agent to discover a shared root.
2. If neither a host-reported shared root nor an approved user-selected supported
   root is supplied, resolve the agent account's existing `Shared` folder with a
   local tool in the isolated Windows agent:

   ```powershell
   $shared = (Resolve-Path -LiteralPath (Join-Path $env:USERPROFILE 'Shared') -ErrorAction Stop).Path
   if (-not (Test-Path -LiteralPath $shared -PathType Container)) {
       throw 'The agent Shared path is not a directory.'
   }
   $shared
   ```

   Use this agent's `USERPROFILE`, not the human user's profile.
   Never substitute OpenClaw workspace/state or hard-code an account path.
3. Verify that the selected root exists and the resolved file destination stays
   inside it; reject parent traversal or links redirecting outside it. Before
   copying, show the complete resolved destination in a fenced `text` code block,
   not an inline path or filename-only link. Offer another
   supported delivery route and respect any destination the user declines.
   Do not offer the human's profile folders as selectable alternatives.
4. Copy rather than move the intended files. Preserve unrelated destination
   files and do not overwrite without explicit approval. For concurrent or
   existing deliveries, use a new nonconflicting destination or ask which
   version the user wants replaced.
5. Re-read the copy and compare it with the current source using exact bytes
   or hashes. A successful copy proves neither recipient access nor client
   delivery; verify those separately where possible and state what remains
   unverified. Say **"Copied to the shared folder at [actual path]; I did not
   write to your Desktop."** when Desktop was requested. Use the requested
   folder name otherwise, and state any unverified recipient access.

**Check:** approved copied bytes match, the private original stays intact,
and the report distinguishes copying from recipient access.

## Verification and recovery

If profile/path resolution, directory availability, access, copying, or client
delivery fails, report the failure and ask for an approved existing supported
shared destination or a supported download/attachment capability. Name the actual
missing or failed destination even when refusing
an unsafe export; do not propose missing `Shared` as an available alternative.
For a filesystem handoff, ask for a supported shared-folder or client delivery route and clarify
intended nonsensitive deliverables only if needed.

Do not create a replacement shared root, fall back to Public Documents or
`PUBLIC`/`TEMP`, modify broad ACLs, use administrator Explorer, or weaken isolation.
For partial delivery, identify each file's result. Retry only the authorized
failed part, without overwriting unrelated files or claiming the whole handoff
succeeded. Keep private originals intact.

**Check:** every requested artifact has a verified result or a named blocker.

## Source freshness

The current tool schemas, connected-client capabilities, and the service's
official sign-in instructions govern execution. Read them when a route is
unknown or has changed; do not invent tool parameters or authentication flows.
`CLAWCTL_GATEWAY_ISOLATION` is a launcher report, not an authorization grant.
Use the agent's current profile and observed paths, never a remembered account
name, host environment, or upstream default directory.
