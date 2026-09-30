# GUI and Sign-In

## Decide who must interact

Local dialogs and browser windows belong to the isolated agent session, not
the user's interactive desktop. Do not launch or offer a local window for
human viewing or input, even if the user asks you to open a sign-in dialog.
Explain the limitation only when it affects the task.

Authorized agent-only GUI work is allowed if no human viewing or input is
needed. Verify that a suitable automation tool is actually available, establish
its execution session, and check its result. Creating a process is not proof
that automation worked. Close only task-created windows or processes when
finished; do not terminate unrelated work.

**Check:** the intended viewer/operator and the tool's actual capabilities are
known before starting GUI work.

## Human participation and authentication

1. Prefer the service's documented CLI, headless, device-code, or text flow.
   If no supported flow is known, obtain its official documentation before
   proposing one. Do not promise that a local window will reach the user.
2. Give the user concise steps to act on their own desktop or through a
   supported connected client. Relay a safe verification URL only when the
   documented flow provides it; relay sensitive verification material only
   through a supported private route to the requesting user.
3. Do not ask for passwords, session cookies, access tokens, recovery codes,
   or MFA secrets in chat. Never fabricate a device-code flow, bypass MFA or
   consent, disable isolation, or change security policy.
4. Wait for the required user action. User confirmation is not by itself proof
   of agent-side authentication: use the service's supported status or account
   check without printing secrets. If it fails or expires, report the actual
   failure and offer the documented retry.

**Check:** the service reports the intended authenticated state, or the
remaining participation/blocker is explicit. Do not claim sign-in from an
opened window or generated code.

## Remote or user-session UI

A connected node may have its own desktop, filesystem, and automation tools.
Before using it, establish the selected node, execution location, available
capability, and authorization for the exact action. Do not transfer an
agent-local file path or assume that `host` means the user's desktop.

Use a verified, authorized UI route when available. Otherwise provide user-side
steps or a supported headless route. Do not install remote-control tools,
change ACLs, elevate Explorer, or escape the isolated session to make a GUI
visible.

**Check:** any user-visible result is supported by evidence from the selected
execution environment, not merely connectivity.
