# Mneme

The memory agent implementation: storage, projections, classification,
distillation, entity resolution, sync. Implements the interfaces declared
in [`Mneme.Contracts`](../Mneme.Contracts/).

## Status

**Not started.** Phase 0 (contracts) lands first; this project lights up
during Phase 1 (event log + SQLite schema).

See [`plans/plan.md`](../../plans/plan.md) for the 11-phase build plan
and [`plans/research-zep-sqlite-deepdive.md`](../../plans/research-zep-sqlite-deepdive.md)
for the schema + SQL patterns this implementation is targeting.

## Planned dependencies (per phase)

| Phase | Adds | Why |
|---|---|---|
| 1 | `Microsoft.Data.Sqlite` | Event log + SQLite storage |
| 1 | `NUlid` (or hand-rolled) | ULID generation for idempotent event IDs |
| 5 | `Microsoft.SemanticKernel` (or MAF) | Pluggable LLM provider for distillation |
| 11 (v2) | `sqlite-vec` extension | Vector search |

## Design rules

- **No public types here without a corresponding interface in `Mneme.Contracts`.**
- **Every database write goes through the ingest path.** No backdoors.
- **SQL lives in source files, not strings scattered through code.**
  All schema and queries in `Storage/Sql/*.sql` embedded resources.
- **Distillation prompts are Apache 2.0-attributed** when ported from
  Graphiti — see [`NOTICE`](../../NOTICE).
