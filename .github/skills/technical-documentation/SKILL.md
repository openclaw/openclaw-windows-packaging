---
name: technical-documentation
description: >-
  Write, review, or audit this repository's technical documentation, including
  README.md, CONTRIBUTING.md, root and scoped AGENTS.md files, docs pages, and
  documentation drift. Use for source-backed documentation authoring,
  behavior verification, and cross-surface consistency. Do not use for code
  review, deslop diff cleanup, or whole-page rewrites, splits, or
  reorganizations handled by docs-refactor.
license: MIT
metadata:
  source: https://github.com/vincentkoc/dotskills
  derivation: Adapted for the OpenClaw Gateway MSIX packaging repository.
---

# Technical Documentation

Produce documentation that lets a reader complete a real task without
guessing at current behavior. Read [principles](references/principles.md)
before drafting or reviewing.

## Operating contract

| Concern | Rule |
|---|---|
| Use when | Writing, reviewing, or auditing any documented repository surface; resolving README, CONTRIBUTING, AGENTS.md, command, or doc-drift claims. |
| Do not use when | Reviewing code behavior (`code-review`), cleaning a diff (`deslop`), or substantially rewriting, splitting, or reorganizing a page (`docs-refactor`). |
| Required inputs | Requested outcome or review scope, target audience, affected surface, and the source authority for each behavior claim. |
| Mutation boundary | Investigate read-only first. Edit only requested documentation surfaces; do not invent commands or behavior, and preserve verified facts unless source proves they are obsolete. |
| Done when | Claims are source-verified, relevant surfaces agree, the exact validation run and lanes not run are reported, and remaining uncertainty is explicit. |

## Workflow

1. Classify the work as **build** (authoring or targeted correction) or
   **review** (finding and reporting or correcting defects). Identify the
   reader, trigger, steps, and observable outcome before naming a structure.
2. Inventory the relevant documented repository surfaces and their ownership
   using [repository surfaces](references/repo-surfaces.md). State which are
   in scope and which are intentionally out of scope.
3. For every behavior claim, locate its listed authority and verify the claim
   in source. Never infer behavior from names, defaults, generated output, or
   maintainer intent. Separate current behavior from planned behavior.
4. Apply the reader funnel: explain what and why, give the smallest safe
   working path, then route to next steps. Select a Diataxis page type:
   tutorial, how-to, reference, or explanation. Treat README sections,
   `docs\*.md` pages, and instruction files as distinct page forms.
5. For contributor and agent instructions, apply
   [instruction-file rules](references/instruction-files.md). Require concrete
   tracked paths, runnable command scope, and explicit `Always`, `Ask first`,
   and `Never` boundaries where operational rules are given.
6. Preserve facts during targeted edits: keep correct commands, constraints,
   safety warnings, prerequisites, expected output, and recovery guidance.
   Correct or remove a claim only with authority-backed evidence.
7. Check links, anchors, tracked paths, line endings, and relevant behavior
   with the commands in [repository surfaces](references/repo-surfaces.md).
   Run the narrowest applicable command; do not name tooling that this
   repository does not provide.
8. Report the surfaces and authorities checked, edits or findings, the exact
   validation command(s) run and result, and an explicit list of validation
   lanes not run.

## Review standard

- Lead with the reader's job, one recommended path, and a smallest reliable
  verification. Put rare internals and exhaustive detail behind focused
  sections or links.
- Make steps imperative and observable: state prerequisites, commands,
  expected outcome, common failure, and recovery when each is relevant.
- Treat missing referenced files, nonexistent commands, stale command scope,
  and cross-surface conflicts as high-priority defects.
- Keep README.md, CONTRIBUTING.md, root and scoped `AGENTS.md` files, and
  `docs\*.md` mutually consistent and aligned with source. Scoped
  `AGENTS.md` files may narrow their tree's rules but must not compete with
  repository-wide ownership or safety rules.

## References

- [Documentation principles and attribution](references/principles.md)
- [Instruction-file drift rules](references/instruction-files.md)
- [Repository surfaces, authorities, and validation](references/repo-surfaces.md)
