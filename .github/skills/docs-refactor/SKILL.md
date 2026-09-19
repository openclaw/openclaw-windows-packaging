---
name: docs-refactor
description: >-
  Refactor a whole documentation page in this repository without losing
  source-verified behavior facts. Use for page rewrites, splits,
  reorganizations, or substantial shortening of README sections, docs pages,
  or instruction files. Do not use for ordinary documentation authoring,
  targeted corrections, or documentation review; use technical-documentation
  instead.
license: MIT
metadata:
  source: https://github.com/vincentkoc/dotskills
  derivation: Adapted for the OpenClaw Gateway MSIX packaging repository.
---

# Documentation Refactor

Read [technical-documentation](../technical-documentation/SKILL.md) first.
A rewrite improves structure; it is never permission to discard behavior facts.

## Operating contract

| Concern | Rule |
|---|---|
| Use when | Rewriting, splitting, merging, reorganizing, or materially shortening a README section, `docs\*.md` page, or root or scoped `AGENTS.md` file. |
| Do not use when | Writing a new page, making a targeted documentation correction, or reviewing documentation without a whole-page restructuring; use `technical-documentation`. |
| Required inputs | Target page or section, desired reader outcome, intended page type, and the authority for every behavior-sensitive claim. |
| Mutation boundary | Inventory the old content before editing. Give every fact exactly one outcome: keep, move to a named destination, or delete with source-backed proof of obsolescence. |
| Done when | The fact inventory is reconciled, old and new content are compared, sources and links are validated, and the final report names every moved fact and destination. |

## Refactor workflow

1. Read `..\technical-documentation\SKILL.md` and its repository-surface
   authority table. Limit page types to README sections, `docs\*.md` pages,
   and instruction files; select the appropriate tutorial, how-to, reference,
   or explanation shape before editing.
2. Build a pre-rewrite fact inventory from the current target: commands,
   flags, defaults, prerequisites, constraints, safety notes, errors,
   recovery steps, examples, expected output, links, and ownership claims.
3. Assign every inventory item exactly one outcome:
   **keep** in the target, **move** to one named existing or new destination,
   or **delete** only when the designated source authority proves it obsolete
   or out of scope. Do not leave an outcome implicit.
4. Verify behavior-sensitive facts against the authorities in
   `..\technical-documentation\references\repo-surfaces.md`. Never infer
   current behavior from the old prose, a name, a default, or intent.
5. Draft around the reader funnel: orient the reader, present one recommended
   path, give an observable check, then route detailed, rare, or reference
   material without burying task-critical safety information.
6. Preserve or repair cross-surface consistency. For instruction files,
   `README.md`, `CONTRIBUTING.md`, root and scoped `AGENTS.md` files, and
   `docs\*.md` must agree with each other and source according to their
   distinct audiences.
7. Perform an old-versus-new comparison. Reconcile each fact-inventory row,
   every command, warning, link, and moved destination before declaring the
   refactor complete.
8. Run the applicable real commands from
   `..\technical-documentation\references\repo-surfaces.md`, including the
   advisory doc-reference check when it is available. Report the exact
   validation command(s) run, result, and lanes not run.

## Final report

Name the target, its selected page type, the source authorities checked, and
each fact moved with its exact destination. Explicitly list facts deleted and
the proof of obsolescence, unresolved uncertainty, validation performed, and
validation lanes not run.
