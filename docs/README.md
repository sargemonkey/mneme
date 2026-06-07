# docs/

Long-lived technical documentation for Mneme. Distinct from `plans/`,
which holds the build plan and one-off research artifacts.

## What goes here

- **Architecture Decision Records (ADRs)** — `docs/adr/NNNN-title.md`,
  one per locked decision. Use the
  [MADR](https://adr.github.io/madr/) template. The first ADRs to write
  are the ones that consolidate the "Locked decisions" table from
  [`AGENTS.md`](../AGENTS.md):
  - `0001-sqlite-as-only-embedded-backend.md`
  - `0002-dotnet-not-python.md`
  - `0003-build-not-adopt-graphiti.md`
  - `0004-append-only-event-log-with-projections.md`
  - `0005-conservative-entity-resolution.md`
  - `0006-workstream-isolation-default.md`
- **Schema docs** — `docs/schema/` for the SQLite DDL once Phase 1
  lands, plus migration notes.
- **Public-API reference** — generated or hand-written reference for
  `Mneme.Contracts` once stable.
- **Integration guides** — `docs/integration/` for "how to wire Mneme
  into your agent host".

## What doesn't go here

- Build plan and roadmap → [`plans/plan.md`](../plans/plan.md).
- Task backlog → [`plans/backlog.md`](../plans/backlog.md).
- Research one-offs → `plans/research-*.md`.
- Onboarding for agents → [`AGENTS.md`](../AGENTS.md) at repo root.
