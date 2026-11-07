# Project-local pstack for Codex

This directory provides a Codex-compatible, project-scoped port of the pstack entrypoint. Invoke it by asking for `pstack` or `poteto mode` in this repository.

Upstream: https://github.com/cursor/plugins/tree/main/pstack

The upstream Cursor plugin declares Cursor-specific metadata, slash commands, agent types, model routing, automations, and integrations. Those cannot be installed as-is in Codex, so this port retains the applicable workflow and verification guidance in `SKILL.md` and maps delegation to Codex's native subagents.

Upstream plugin metadata identified version `0.15.36` at the time of porting. The upstream project is MIT licensed.
