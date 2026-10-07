---
name: windows-agent-handoff
description: Handle user-facing GUI, sign-in, and file handoffs from an isolated Windows agent session; not ordinary headless work.
metadata: {"openclaw":{"os":["win32"],"requires":{"env":["CLAWCTL_GATEWAY_ISOLATION"]}}}
---

# Windows Agent Handoff

## Operating contract

| Concern | Rule |
|---|---|
| Use when | A task in the isolated Windows agent needs human viewing/input, sign-in participation, user-session GUI, or delivery of a generated file. |
| Do not use when | Ordinary headless work stays inside the agent session, or an established remote-node workflow needs no local-to-user handoff. This skill does not provision sessions or administer the host. |
| Required inputs | Execution location, launcher isolation report, intended user action or exact deliverable, available client/tool capabilities, and any selected destination. |
| Mutation boundary | Inspect first. Preview the handoff; act only within the user's request and approved destination. Never weaken isolation, change ACLs, collect credentials, or export private workspaces. |
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

2. Select the smallest supported route.
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
Before using it, establish the selected node, execution location, available
capability, and authorization for the exact action. Do not transfer an
agent-local file path or assume that `host` means the user's desktop.

Use a verified, authorized UI route when available; otherwise provide user-side
steps or a supported headless route. Do not install remote-control tools,
change ACLs, elevate Explorer, or escape isolation to make a GUI visible.

**Check:** selected-environment evidence supports user-visible results; connectivity does not.

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
accepted attachment submission from verified receipt.

**Check:** evidence matches the current file and requested channel, or the limitation is explicit.

## Filesystem copy

1. Prefer a host-reported or user-selected destination. A host-reported path
   alone does not prove recipient access. Do not run host lifecycle commands
   inside the agent to discover it.
2. If neither is supplied, resolve the agent account's existing `Shared`
   folder with a local tool in the isolated Windows agent:

   ```powershell
   $shared = (Resolve-Path -LiteralPath (Join-Path $env:USERPROFILE 'Shared') -ErrorAction Stop).Path
   if (-not (Test-Path -LiteralPath $shared -PathType Container)) {
       throw 'The agent Shared path is not a directory.'
   }
   $shared
   ```

   Use this agent's `USERPROFILE`, not the human user's profile.
   Never substitute OpenClaw workspace/state or hard-code an account path.
3. Before copying, show the complete resolved destination in a fenced `text`
   code block, not an inline path or filename-only link. Offer another
   destination and respect any destination the user declines.
4. Copy rather than move the intended files. Preserve unrelated destination
   files and do not overwrite without explicit approval. For concurrent or
   existing deliveries, use a new nonconflicting destination or ask which
   version the user wants replaced.
5. Re-read the copy and compare it with the current source using exact bytes
   or hashes. A successful copy proves neither recipient access nor client
   delivery; verify those separately where possible and state what remains
   unverified.

**Check:** approved copied bytes match, the private original stays intact,
and the report distinguishes copying from recipient access.

## Verification and recovery

If profile/path resolution, directory availability, access, copying, or client
delivery fails, report the failure and ask for a supported destination or
capability. Name the actual missing or failed destination even when refusing
an unsafe export; do not propose missing `Shared` as an available alternative.
For a filesystem handoff, ask for a supported destination and clarify intended
nonsensitive deliverables only if needed.

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
