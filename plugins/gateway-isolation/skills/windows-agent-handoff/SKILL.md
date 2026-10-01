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
   - Set `{baseDir}` to the directory containing the advertised `SKILL.md`
     you read. Resolve reference paths against that directory, never the
     application root, workspace, or current working directory.
   - Read the applicable branch before answering, including refusals and
     recovery guidance. Unsafe requested actions remain prohibited during
     this read-only preparation.
   - For GUI work, including agent-only automation, browser interaction,
     sign-in, or other human participation,
     read [GUI and sign-in](references/gui-and-sign-in.md).
   - For sending, downloading, opening, or copying a generated deliverable,
     read [file handoff](references/file-handoff.md).
   - When both are needed, follow both branches. Keep unrelated headless work
     in the agent session; do not repeat the isolation explanation every turn.
   - The branch files are `{baseDir}\references\gui-and-sign-in.md` and
     `{baseDir}\references\file-handoff.md`; do not guess another `skills`
     directory if a read fails.
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
   - Separate agent-side execution, recipient filesystem access, and client
     attachment/open/download evidence. None implies the others.
   - State what completed, what evidence supports it, and any unverified
     access or delivery. When blocked, name the next supported user action.
   - **Check:** the report does not claim a visible window, completed sign-in,
     or delivered file solely because a local command succeeded.

## Source freshness

The current tool schemas, connected-client capabilities, and the service's
official sign-in instructions govern execution. Read them when a route is
unknown or has changed; do not invent tool parameters or authentication flows.
`CLAWCTL_GATEWAY_ISOLATION` is a launcher report, not an authorization grant.
Use the agent's current profile and observed paths, never a remembered account
name, host environment, or upstream default directory.
