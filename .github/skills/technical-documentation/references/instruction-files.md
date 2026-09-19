# Instruction-File Drift Rules

This repository uses a root `AGENTS.md` for repository-wide working, ownership,
validation, and safety rules, with narrower `AGENTS.md` files under `src\`,
`scripts\`, and `tests\`. It has no alternate agent-instruction file, alias,
or symlink.

Copilot instruction surfaces can combine root and applicable scoped
`AGENTS.md` files, and some surfaces give the nearest file precedence. Every
scoped file must therefore direct the reader to the root file, remain
sufficient for work in its tree, and avoid conflicting with repository-wide
rules. Put a rule at the narrowest owner that can state it without duplicating
repository-wide policy.

## Required alignment

Treat `README.md`, `CONTRIBUTING.md`, the root and scoped `AGENTS.md` files,
and `docs\*.md` as one contract with distinct audiences. They must agree with
each other and with the repository authority that owns each behavior claim.

1. Verify every path with `git ls-files`, not a filesystem listing.
2. Verify each command is real, runnable from the stated scope, and has the
   stated prerequisites and arguments.
3. Keep command references in `CONTRIBUTING.md` or the owning focused doc.
   Agent instructions select the required lane and call out execution traps;
   they do not duplicate the command catalog.
4. Use concrete paths and commands, never vague placeholders.
5. State operational boundaries with `Always`, `Ask first`, and `Never` when
   an agent could otherwise perform an unsafe or irreversible action.
6. Keep repository-wide ownership and safety policy in root `AGENTS.md`.
   Scoped files may narrow implementation and proof rules but never weaken or
   contradict the root.

Treat a missing referenced file, nonexistent command, command-scope mismatch,
or scoped rule that silently replaces a required root rule as a high-priority
defect. A command that works only from a subdirectory must say so; do not
present it as a repository-root command.

## Conflict matrix

| Conflict | Resolve by |
|---|---|
| A behavior claim differs from source | Correct documentation to the designated source authority; report the drift. |
| README.md and CONTRIBUTING.md differ | Use the authority to resolve the fact, then align both according to their reader roles. |
| Root AGENTS.md differs from a scoped AGENTS.md | Preserve the root ownership or safety invariant and narrow only the tree-specific implementation detail. |
| An AGENTS.md command differs from its human-facing owner | Verify the real command, correct its human-facing owner, then reduce the AGENTS.md entry to lane selection or an execution trap. |
| A command is real but wrongly scoped | State its required working directory or replace it with the authoritative root-scoped command. |
| A referenced path is absent from tracked files | Remove or repair the reference; never rely on untracked output. |
| No authority resolves the conflict | Preserve the uncertainty, avoid a behavior claim, and identify the missing evidence. |
