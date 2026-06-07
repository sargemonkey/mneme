# GitHub Copilot — Mneme repo instructions

> **Read [`AGENTS.md`](../AGENTS.md) first.** It is the canonical
> onboarding doc for all AI agents (Copilot included). This file
> exists so GitHub Copilot picks up the conventions automatically; it
> does not replace `AGENTS.md`.

## TL;DR for Copilot

- **Language / target**: C# / .NET 8. The solution is `Mneme.slnx`.
- **Status**: design phase, no implementation code yet. The current task
  is almost certainly **Phase 0 — Contracts**. See
  [`plans/backlog.md`](../plans/backlog.md).
- **Where to write code**:
  - Interfaces + DTOs → `src/Mneme.Contracts/` (BCL deps only).
  - Memory agent implementation → `src/Mneme/`.
  - MCP server wrapper → `src/Mneme.Mcp/`.
  - Tests → `tests/Mneme.Contracts.Tests/` and `tests/Mneme.Tests/`.
- **Build / test**: `dotnet build Mneme.slnx` and `dotnet test Mneme.slnx`.
- **Warnings are errors.** Nullable enabled. File-scoped namespaces.
- **Commit style**: Conventional Commits. Include
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`
  on any commit you co-author.

## Hard rules

1. `Mneme.Contracts` may not depend on anything outside the .NET 8 BCL.
2. The event log (`memory_events`) is **append-only**. No UPDATE/DELETE.
3. Every public type ships with at least one test.
4. Don't relitigate locked decisions (see `AGENTS.md` → "Locked decisions").
5. Porting a Graphiti prompt? Update `NOTICE` in the same commit.

## Useful planning docs

- [`AGENTS.md`](../AGENTS.md) — full conventions, read order, locked decisions.
- [`plans/backlog.md`](../plans/backlog.md) — dependency-ordered tasks.
- [`plans/plan.md`](../plans/plan.md) — long-form design.
- [`plans/research-zep-sqlite-deepdive.md`](../plans/research-zep-sqlite-deepdive.md) — schema + SQL pattern blueprint.
