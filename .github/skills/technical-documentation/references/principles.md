# Documentation Principles

This skill is derived from the MIT-licensed `technical-documentation` skill
published at https://github.com/vincentkoc/dotskills. It adapts that material
to this repository and intentionally omits documentation-site-specific
machinery.

## Matt Palmer: 8 rules for better docs

Source: https://mattpalmer.io/posts/2025/10/8-rules-for-better-docs/

1. Write for humans, optimize for agents.
2. Start with a funnel: what and why, quickstart, then next steps.
3. Use Diataxis to scaffold content.
4. Write with AI, but structure for agents.
5. Offload routine documentation operations to background agents.
6. Automate quality with CI.
7. Automate scaffolding and repetitive workflow tasks.
8. Make contribution easy and visible.

## OpenAI cookbook quality constraints

Source: https://cookbook.openai.com/articles/what_makes_documentation_good

- Prefer specific, accurate terminology over niche jargon.
- Keep examples self-contained and minimize dependencies.
- Prioritize high-value topics over edge-case depth.
- Do not teach unsafe patterns, including exposed secrets.
- Open with context that orients readers quickly.
- Apply empathy and override rigid rules when doing so clearly improves the
  reader's outcome.

## Conflict-resolution merge policy

When guidance conflicts, decide in this order:

1. Preserve the reader's ability to complete the task safely.
2. Preserve factual correctness against the designated repository authority.
3. Preserve clear, scannable structure and the page's Diataxis purpose.
4. Preserve long-term maintainability and cross-surface consistency.
5. Optimize for agent consumption only when it does not reduce human clarity.

Record unresolved source conflicts instead of selecting an interpretation.
Do not use multilingual-parity requirements: this repository's documented
surface set has no translated documentation workflow.
