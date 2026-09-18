# Instruction-File Drift Rules

This repository has one agent instruction surface:
`.github\copilot-instructions.md`. It has no `AGENTS.md`, `CLAUDE.md`,
`.cursorrules`, alias, symlink, or alternate agent-rule ecosystem to inventory.

## Required alignment

Treat `.github\copilot-instructions.md`, `CONTRIBUTING.md`, and `README.md`
as a three-way contract. They must agree with each other and with the
repository authority that owns each behavior claim.

1. Verify every path with `git ls-files`, not a filesystem listing.
2. Verify each command is real, is runnable from the stated scope, and has the
   stated prerequisites and arguments.
3. Use concrete paths and commands, never vague placeholders.
4. State operational boundaries with `Always`, `Ask first`, and `Never` when
   a reader or agent could otherwise perform an unsafe or irreversible action.
5. Keep contributor workflow, test requirements, formatting policy, and PR
   rules consistent across the three surfaces.

Treat a missing referenced file, nonexistent command, or command-scope
mismatch as a high-priority defect. A command that works only from a
subdirectory must say so; do not present it as a repository-root command.

## Conflict matrix

| Conflict | Resolve by |
|---|---|
| A behavior claim differs from source | Correct documentation to the designated source authority; report the drift. |
| README.md and CONTRIBUTING.md differ | Use the authority to resolve the fact, then align both according to their reader roles. |
| `.github\copilot-instructions.md` differs from either human-facing surface | Use the authority to resolve the fact, then align all three without duplicating unsupported policy. |
| A command is real but wrongly scoped | State its required working directory or replace it with the authoritative root-scoped command. |
| A referenced path is absent from tracked files | Remove or repair the reference; never rely on untracked output. |
| No authority resolves the conflict | Preserve the uncertainty, avoid a behavior claim, and identify the missing evidence. |
