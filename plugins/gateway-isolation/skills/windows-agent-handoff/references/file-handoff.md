# File Handoff

## Establish the deliverable and requested channel

Identify the exact current file, intended recipient, and requested outcome:
an in-chat attachment, client download/export, or recipient-accessible
filesystem copy. Verify the file exists and contains the intended output.
Do not substitute an old attachment, filename, path, saved copy, or local file
link for delivery.

Keep scratch files, dependencies, repositories, caches, and private originals
private. Hand off only the requested nonsensitive deliverables, not a whole
workspace or credential database. If content may contain secrets or private
configuration, resolve that before exporting it.

**Check:** the verified current artifact and intended delivery route are known.

## Client attachment or export

Use the current client's documented attachment/export capability and the
actual tool schema. Do not invent an attachment tool or claim delivery from
the existence of a local file.

If the capability is unavailable, explain that in-chat delivery is blocked and
offer a supported alternative. Do not silently switch to a filesystem copy.
Verify the attachment/export result identifies the current artifact; verify
recipient-side open/download and exact bytes when that evidence is available.
If it is not, distinguish accepted attachment submission from verified receipt.

**Check:** delivery evidence corresponds to the current file and requested
channel, or the limitation is explicit.

## Filesystem copy

1. Prefer a host-reported or user-selected destination. A host-reported path
   does not by itself prove recipient access. Do not run host lifecycle
   commands inside the agent to discover it.
2. If neither is supplied, resolve the agent account's existing `Shared`
   folder with a local tool in the isolated Windows agent:

   ```powershell
   $shared = (Resolve-Path -LiteralPath (Join-Path $env:USERPROFILE 'Shared') -ErrorAction Stop).Path
   if (-not (Test-Path -LiteralPath $shared -PathType Container)) {
       throw 'The agent Shared path is not a directory.'
   }
   $shared
   ```

   Use this agent's `USERPROFILE`, not the human user's profile. Do not
   substitute the OpenClaw workspace/state directory or hard-code an account
   path.
3. Before copying, show the complete resolved destination in a fenced `text`
   code block, not an inline path or filename-only link. Say the user can
   choose another destination; respect any destination they decline.
4. Copy, rather than move, the intended files. Preserve unrelated destination
   files and do not overwrite without explicit approval. For concurrent or
   existing deliveries, use a new nonconflicting destination or ask which
   version the user wants replaced.
5. Re-read the copy and compare it with the current source, using exact bytes
   or hashes as appropriate. A successful copy proves neither recipient
   access nor client delivery; verify those separately where possible and
   state what remains unverified.

**Check:** the approved copy contains the intended bytes, the private original
is preserved, and the report distinguishes copying from recipient access.

## Missing paths, permission denial, and partial results

If profile/path resolution, directory availability, access, copying, or client
delivery fails, report the failure and ask for a supported destination or
capability. Do not create a replacement shared root, fall back to Public
Documents or `PUBLIC`/`TEMP`, modify broad ACLs, use administrator Explorer, or
weaken isolation.

If some files were delivered and others failed, identify each result. Retry
only the authorized failed part, without overwriting unrelated files or
claiming the whole handoff succeeded. Keep private originals intact.

**Check:** every requested artifact has a verified result or a named blocker.
