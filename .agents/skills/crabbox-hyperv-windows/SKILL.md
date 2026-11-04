---
name: crabbox-hyperv-windows
description: >-
  Prepare a private copy of a generalized Windows Gen2 VHDX with a known local
  administrator and optional OOBE priming for local Crabbox Hyper-V. Not for ISO
  conversion, cloud/Linux, repository test execution, or package deployment.
license: MIT
---

# Prepare a local Windows Crabbox template

Consume a user-owned Gen2/UEFI Windows VHDX and create a **new private copy**.
Never edit a stable parent image already used by leases. If the user has only
an ISO, use [windows-iso-to-vhdx](../windows-iso-to-vhdx/SKILL.md) first; it
also owns public ISO download guidance.

## Operating contract

| Concern | Contract |
| --- | --- |
| Use when | Preparing a Windows VHDX copy with a known local administrator and OOBE readiness for the Crabbox `hyperv` provider. |
| Do not use when | ISO conversion belongs to `windows-iso-to-vhdx`; running this repository's configured jobs belongs to [crabbox](../crabbox/SKILL.md). No Linux/cloud, hydration, package install, or host account changes. |
| Required inputs | Detached standalone generalized GPT Windows VHDX, new private output directory, verified Crabbox executable, Hyper-V switch, and securely supplied administrator password. |
| Mutation boundary | Inspect/WhatIf first. Actual copy/mount/account injection and optional priming VM require explicit task authority and an elevated PowerShell terminal. Never log credentials, modify the source/template/other VMs, or create a SYSTEM task. |
| Done when | Distinguish injected-only from primed: `IMAGE_STATE_COMPLETE`, administrator PowerShell Direct login, graceful shutdown, exact temporary-VM cleanup, detached output, and unchanged source are required for primed success. |

## Inspect and pin

Use Windows with Hyper-V, Storage tools, PowerShell 7, and a switch the user
has selected. Offline mounting requires **Run as administrator**; normal
Crabbox execution runs as the invoking user with Hyper-V permissions.
Desktop operations require an interactive guest session, not a LocalSystem
host process. Do not create unattended SYSTEM tasks to hide failures.

Resolve the exact available binary before elevation, verify `--version`,
`providers --json`, and its SHA-256, and check its public release provenance.
Do not download or silently substitute another binary. The public contract
was verified with published **v0.81.0**; check version-specific
[Hyper-V docs](https://github.com/openclaw/crabbox/blob/v0.81.0/docs/providers/hyperv.md)
before using a different version.

This helper expects a generalized installation that will process the
`oobeSystem` answer file on first boot. An already-specialized disk will not
replay OOBE; do not report an injected file as a working account. Use the
existing valid account/template instead, or start with suitable installation
media. It refuses to overwrite an existing answer file.

## Prepare the private copy

Run the [template helper](scripts/Initialize-CrabboxHypervWindowsTemplate.ps1)
from the repository root, first with `-WhatIf`:

```powershell
& .\.agents\skills\crabbox-hyperv-windows\scripts\Initialize-CrabboxHypervWindowsTemplate.ps1 `
    -VhdxPath $sourceVhdx -OutputDirectory $newPrivateDirectory `
    -CrabboxPath $crabboxPath -Switch $switchName -WhatIf
```

For an explicitly requested operation, prompt locally—never in chat or as a
plaintext command argument—and pass the secure object:

```powershell
$guestPassword = Read-Host 'Template administrator password (retain securely)' -AsSecureString
& .\.agents\skills\crabbox-hyperv-windows\scripts\Initialize-CrabboxHypervWindowsTemplate.ps1 `
    -VhdxPath $sourceVhdx -OutputDirectory $newPrivateDirectory `
    -CrabboxPath $crabboxPath -Switch $switchName `
    -AdminPassword $guestPassword -Prime
```

The new output directory and copied VHDX each have a verified private ACL for
their creator, Administrators and SYSTEM. A strong password is required; none is generated, returned or
printed. The [helper functions](scripts/WindowsTemplate.Helpers.ps1) create
the public Windows Setup local-account answer file and bound readiness probes.
The [module](scripts/WindowsTemplate.psm1) owns copy/mount/VM lifecycle checks.
Unattend password encoding is **reversible obfuscation**, not encryption.
Windows Setup may retain credential-bearing material in the image. Keep the
whole image private, never upload it as proof, and retain the password through
the user's secret manager. Local administrators can access the template.
The helper does not persist a host credential file or environment variable.

`-Prime` creates exactly one task-owned Gen2 VM, uses normal Secure Boot and
vTPM configuration, and waits for authenticated administrator access plus
`IMAGE_STATE_COMPLETE`. It does not bypass Windows hardware/licensing checks.
OOBE may require the user to accept terms or resolve UI prompts; a timeout
leaves an explicitly **unqualified** output, never a success-shaped fallback.
No sleep duration alone proves readiness.

The source is copied, not mounted or modified. On completion the source hash
is rechecked. The temporary VM is removed by its exact ID while the private
copy remains. Failed priming may force off only that owned VM; do not treat
the retained image as qualified. Inspect recovery diagnostics before retrying,
and choose a new output directory; do not overwrite partial state.

## Use the template separately

Keep the primed VHDX immutable while Crabbox leases use it as their
differencing-disk parent. Set the supported machine-local image/user/switch
settings outside Git. Supply `CRABBOX_HYPERV_GUEST_PASSWORD` only in the
invoking process from secure input; clear/restore it in `finally`. Environment
variables contain plaintext in process memory and are visible to privileged
processes; never echo or persist the value.

Crabbox owns per-lease verified OpenSSH/key bootstrap and Node/npm baseline.
Do not add `FirstLogonCommands` OpenSSH installations or features-on-demand
workarounds to the template. Desktop is separately requested with `--desktop`;
acquisition must verify an active guest user/Explorer session. It does not
qualify repository PowerShell/.NET toolchains. Normal native Windows
[single-regular-file SFTP copy](https://github.com/openclaw/crabbox/blob/v0.81.0/docs/commands/cp.md)
is supported, not recursive directory/archive `cp`.

Use the [local Windows guide](../../../docs/crabbox-local-windows.md) and
`crabbox` project skill for separately authorized jobs, exact-ID lease cleanup,
and proof. A primed template is not configured-job proof; Node smoke is not
.NET/quality proof, and a high Windows build number is not IsolationSession
eligibility.

## Sources and freshness

Reviewed 2026-11-04 against the public v0.81.0 provider contract and
[Windows LocalAccount password schema](https://learn.microsoft.com/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-useraccounts-localaccounts-localaccount-password).
Refresh the actual release/docs/help before assuming newer bootstrap,
desktop, or file-copy support. No private services, host-specific paths,
credentials, or preselected image builds are required.
