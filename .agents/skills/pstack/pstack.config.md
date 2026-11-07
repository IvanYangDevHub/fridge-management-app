# pstack setup for this Codex project

## Role defaults

- **Coordinator and integration.** The primary Codex agent owns scope, repository changes, review, and the final report.
- **Investigation.** Use read-only source inspection, tests, logs, and documentation. Delegate only for independent evidence-gathering slices.
- **Implementation.** Make the smallest change that addresses the demonstrated cause. Name the data shape before changing stateful code.
- **Review.** Inspect the final diff for behavior, type safety, boundary validation, migration completeness, and unnecessary complexity.
- **Verification.** Run the narrowest meaningful checks first. Run broader project checks when the affected surface or risk justifies them. Report unavailable checks explicitly.

## Codex execution policy

- Use the current Codex model by default. Do not request or assume Cursor model identifiers.
- Use subagents only for explicitly requested parallelism or genuinely independent, non-overlapping work. The coordinator reviews every result.
- Do not assume Cursor slash commands, Cursor agent types, Cursor automations, or external MCP integrations are available.
- Follow repository instructions such as `AGENTS.md`, package scripts, and CI configuration when they exist.

## Verification policy

1. Inspect the changed files and their callers.
2. Run focused lint, typecheck, unit, or integration commands exposed by the repository when available.
3. For visible UI behavior, exercise or inspect the rendered surface when a supported local surface is available.
4. Inspect the final diff. Do not call work complete based only on a successful compilation or an agent report.

## Configuration status

The project technology stack and scripted checks have not yet been detected. The first implementation task must inspect the repository manifest and CI files, then use the discovered commands instead of inventing a test command.
