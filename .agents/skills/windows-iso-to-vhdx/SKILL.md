---
name: windows-iso-to-vhdx
description: >-
  Create a new offline Windows Gen2/UEFI VHDX from a user-provided installation
  ISO, or guide a public Windows ISO download. Not for Crabbox account/OOBE
  preparation, existing disk maintenance, host installation, or Linux images.
license: MIT
---

# Windows ISO to VHDX

Create a new dynamically expanding GPT Windows disk without installing Windows
on the host or starting a VM. Accept an existing ISO directly; inspect technical
metadata, not its download history. Do not demand a source URL or checksum.

## Operating contract

| Concern | Contract |
| --- | --- |
| Use when | Converting Windows installation media to a new offline Gen2/UEFI VHDX, or finding public ISO media for that task. |
| Do not use when | Existing VHDX account/OOBE preparation belongs to [crabbox-hyperv-windows](../crabbox-hyperv-windows/SKILL.md); no host installation, existing disk repair, Linux, or cloud provisioning. |
| Required inputs | Existing local ISO and new output path/directory; edition only if multiple images remain ambiguous. Default virtual capacity: 80 GiB. |
| Mutation boundary | Read-only/WhatIf unless conversion is explicitly requested. Explain elevation once; never bypass UAC, overwrite a disk, initialize a physical disk, change host BCD, disable protection, boot a VM, or bypass hardware/licensing requirements. |
| Done when | Helper returns verified layout/boot-file/detached-disk metadata; report **offline-prepared, not boot-tested** and no downstream qualification. |

## Prepare

1. If no ISO was supplied, read the [public download guide](references/iso-download.md).
   Prefer the public Windows Insider Preview page with membership/sign-in and
   preview-risk disclosures. Offer stable media when stability is preferred or
   enrollment is declined. Users authenticate themselves in the browser.
2. Resolve a local existing output directory or unused `.vhdx` path. Default
   to Gen2/UEFI and 80 GiB, subject to current Windows and workload requirements.
   Dynamic capacity does not reserve free host storage; allow space for the
   expanded image and headroom. Do not ask for eventual VM/account details.
3. Require Windows, PowerShell 7, Hyper-V management/Storage cmdlets, DISM and
   BCDBoot. Actual disk work needs an elevated Administrators token. Give a
   single heads-up and use an ordinary **Run as administrator** PowerShell
   terminal; do not require a separate elevation skill or enable features.
   Windows Home lacks the normal client Hyper-V role.

## Convert

Use [New-WindowsIsoVhdx.ps1](scripts/New-WindowsIsoVhdx.ps1), whose
[module](scripts/WindowsIsoVhdx.psm1) and
[helpers](scripts/WindowsIsoVhdx.Helpers.ps1) own image selection and GPT layout:

```powershell
& .\.agents\skills\windows-iso-to-vhdx\scripts\New-WindowsIsoVhdx.ps1 `
    -IsoPath $isoPath -VhdxPath $newVhdxPath -ImageName 'Windows 11 Enterprise' -WhatIf
```

Only remove `-WhatIf` when actual conversion is authorized and the elevated
terminal is ready. `-ImageIndex` is an alternative to exact `-ImageName`, not
an additional selector. Infer an edition only when the request/filename names
one, then verify it against the ISO's actual images; never silently choose an
N edition. Omit the selector for a single-image ISO. Ambiguous media fails
before creating the disk and lists available indexes/names.

The helper accepts `install.wim` or `install.esd`, not split SWM. It verifies
the new VHDX's disk identity and RAW state before GPT initialization, creates
300 MiB EFI / 16 MiB MSR / Windows partitions, and calls BCDBoot with explicit
`/s` on that disk's EFI volume and `/f UEFI`, never the host system partition.
It leaves **Defender unchanged**; no exclusions or antivirus opt-out exist.
If scanning costs time, allow it rather than silently weakening protection.

On failure, owned mounts are detached and any partial output is retained with
a warning. Do not overwrite or automatically delete that output. Preexisting
ISO mounts are left mounted. An incompatible DISM/BCDBoot needs a suitable
public Windows ADK/tool version, not hidden error suppression.

## Report and stop

Report the returned image index/name, architecture, version, languages, size,
output path, and verification state. It has no WinRE, credentials, activation,
or unattended-account configuration. Do not hash the ISO unless requested.

Bootability, Windows requirements (including Secure Boot/vTPM), OOBE and
feature eligibility need separately authorized validation. A higher build
number alone does not establish IsolationSession support.

## Sources and freshness

Reviewed 2026-11-04. Refresh the public download page and technical requirements
for the selected image; offered flights, signed URLs and deployment-tool
compatibility change. Never hard-code a flight/CDN URL.

- [Windows requirements](https://www.microsoft.com/windows/windows-11-specifications)
- [UEFI/GPT deployment](https://learn.microsoft.com/windows-hardware/manufacture/desktop/configure-uefigpt-based-hard-drive-partitions)
- [DISM image options](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-image-management-command-line-options-s14)
- [BCDBoot](https://learn.microsoft.com/windows-hardware/manufacture/desktop/bcdboot-command-line-options-techref-di)
