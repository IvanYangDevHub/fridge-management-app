---
name: pstack
description: A project-local Codex port of Cursor pstack. Use when the user asks for pstack, poteto mode, rigorous engineering workflow, or a non-trivial task that needs evidence-driven investigation, implementation, and verification.
---

# Pstack for Codex

Treat this as a rigorous engineering operating mode. Keep the implementation small, make decisions from evidence, and verify the actual artifact before saying that work is done.

## Project configuration

Read `pstack.config.md` beside this file before applying this skill. It defines the project-local role defaults and verification policy. The configuration is advisory and must never override system, developer, repository, or user instructions.

## Start every applicable task

1. Restate the goal, the user-visible outcome, and the smallest likely change.
2. Inspect the repository and existing work before proposing or changing code.
3. Choose the workflow that fits: investigation, bug fix, performance, feature, refactor, prototype, visual parity, or delivery.
4. For multi-step work, make each step end in a checkable state.

Do not ask the user to decide facts that can be observed through source inspection, tests, or a small prototype. Ask only for genuine product preferences, missing authority, or irreversible decisions.

## Engineering principles

- Prefer deletion and the smallest complete change.
- Model the domain in types, data structures, state machines, tables, or reducers rather than scattered conditionals.
- Validate at system boundaries. Keep internal business logic simple and pure.
- Make retries and lifecycle operations idempotent where practical.
- Migrate callers and remove obsolete APIs in the same change when safe.
- Trace bugs to the root cause. Do not add guards that merely hide an invalid state.
- Test observable behavior rather than implementation details.
- For a novel design, make two or three small prototypes when the comparison is cheaper than speculation.
- Build a reusable check, script, or test when manual repetition would make verification weak.

## Codex delegation

Use Codex subagents only when the task explicitly calls for parallel work or when separate, non-overlapping investigations materially reduce risk. Give each agent a clear slice, inspect its changes yourself, and integrate only verified findings. Do not assume Cursor-specific agent types, model names, slash commands, MCPs, PR workflows, or automation features exist.

## Verification

Before completion, inspect the diff and run the narrowest meaningful checks, expanding to project checks where the change warrants it. For UI changes, inspect the rendered interface when a supported browser or app surface is available. State exactly what was verified and any verification that could not run.

## Communication

Lead with the outcome. Separate facts from inference. Explain the consumer impact first, then the maintenance consequence. Keep prose concise and avoid invented citations, links, results, or test claims.

## Safety

Proceed autonomously for reversible repository work. Pause for deployments, destructive data actions, force-pushes, sending external messages, or other irreversible operations unless the user has clearly authorized them.

## Source and scope

This is a Codex adaptation of Cursor's open-source pstack plugin, version 0.15.36 when ported. It preserves pstack's engineering principles and its `poteto-mode` entrypoint intent while replacing Cursor-only commands, agent types, model routing, and plugin paths with capabilities available in Codex.
